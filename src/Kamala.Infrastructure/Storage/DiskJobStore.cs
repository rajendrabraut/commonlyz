using System.Collections.Concurrent;
using System.Security.Cryptography;
using Kamala.Application;
using Kamala.Application.Abstractions;
using Kamala.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kamala.Infrastructure.Storage;

/// <summary>
/// Keeps each job in its own folder under the work directory. Results are reachable only through 128-bit random
/// ids (the id is the capability: nobody can list or guess other people's files) and are deleted after the
/// retention period. Nothing is kept across restarts.
/// </summary>
public sealed class DiskJobStore : IJobStore
{
    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    private readonly ConcurrentDictionary<string, PublishedFile> _files = new();
    private readonly StorageOptions _options;
    private readonly TimeProvider _clock;

    private const string Marker = ".kamala";

    public DiskJobStore(IOptions<StorageOptions> options, TimeProvider clock)
    {
        _options = options.Value;
        _clock = clock;
        var root = new DirectoryInfo(_options.WorkDirectory);

        // Leftovers from a previous run are never served, so start clean, but only ever wipe a folder this store
        // created (it carries the marker file). A misconfigured path to real data stops startup instead.
        if (root.Exists && root.EnumerateFileSystemInfos().Any())
        {
            if (!File.Exists(Path.Combine(root.FullName, Marker)))
                throw new InvalidOperationException($"Files:Storage:WorkDirectory '{root.FullName}' is not empty and was not created by Kamala.");
            root.Delete(recursive: true);
        }
        root.Create();
        File.WriteAllText(Path.Combine(root.FullName, Marker), "Scratch folder of Kamala. Deleted on every start.");
    }

    public IJobWorkspace Create()
    {
        var id = NewId();
        var job = new Job(id, Path.Combine(_options.WorkDirectory, id), _clock.GetUtcNow() + _options.Retention);
        _jobs[id] = job;
        return job;
    }

    public JobResult Publish(IJobWorkspace workspace, IReadOnlyList<OutputFile> outputs)
    {
        var job = (Job)workspace;
        InvalidInputException.ThrowIf(outputs.Count == 0, "Nothing was produced. Check the page range and options.");

        var files = new List<ResultFile>(outputs.Count);
        foreach (var output in outputs)
        {
            var id = NewId();
            var name = Path.GetFileName(output.Path);
            var published = new PublishedFile(output.Path, name, output.Kind.ContentType(), job.JobId);
            _files[id] = published;
            job.FileIds.Add(id);
            files.Add(new ResultFile(id, name, published.ContentType, new FileInfo(output.Path).Length,
                output.Width, output.Height, output.Source?.OriginalName, output.Source?.Size));
        }

        // Uploads and intermediate files are no longer needed once results exist.
        TryDelete(job.InputDirectory);
        TryDelete(job.ScratchDirectory);
        return new JobResult(job.JobId, files, job.ExpiresAt);
    }

    public void Discard(IJobWorkspace workspace) => Remove((Job)workspace);

    public StoredFile? FindFile(string fileId) =>
        _files.TryGetValue(fileId, out var file) && _jobs.TryGetValue(file.JobId, out var job) && job.ExpiresAt > _clock.GetUtcNow() && File.Exists(file.Path)
            ? new StoredFile(file.Path, file.Name, file.ContentType)
            : null;

    public IReadOnlyList<StoredFile> FindJobFiles(string jobId) =>
        _jobs.TryGetValue(jobId, out var job) && job.ExpiresAt > _clock.GetUtcNow()
            ? job.FileIds.Select(FindFile).OfType<StoredFile>().ToList()
            : [];

    /// <summary>Deletes every expired job; called periodically by <see cref="JobJanitor"/>.</summary>
    public int RemoveExpired()
    {
        var now = _clock.GetUtcNow();
        var expired = _jobs.Values.Where(j => j.ExpiresAt <= now).ToList();
        foreach (var job in expired)
            Remove(job);
        return expired.Count;
    }

    private void Remove(Job job)
    {
        _jobs.TryRemove(job.JobId, out _);
        foreach (var id in job.FileIds)
            _files.TryRemove(id, out _);
        TryDelete(job.Root);
    }

    private static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // A download may still hold the file open; the janitor retries on its next pass.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record PublishedFile(string Path, string Name, string ContentType, string JobId);

    private sealed class Job : IJobWorkspace
    {
        private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
        private int _scratchCount;

        public Job(string id, string root, DateTimeOffset expiresAt)
        {
            JobId = id;
            Root = root;
            ExpiresAt = expiresAt;
            InputDirectory = Directory.CreateDirectory(System.IO.Path.Combine(root, "in")).FullName;
            OutputDirectory = Directory.CreateDirectory(System.IO.Path.Combine(root, "out")).FullName;
            ScratchDirectory = Directory.CreateDirectory(System.IO.Path.Combine(root, "tmp")).FullName;
        }

        public string JobId { get; }
        public string Root { get; }
        public DateTimeOffset ExpiresAt { get; }
        public string InputDirectory { get; }
        public string OutputDirectory { get; }
        public string ScratchDirectory { get; }
        public ConcurrentBag<string> FileIds { get; } = [];

        public string ReserveOutputPath(string fileName)
        {
            var extension = System.IO.Path.GetExtension(fileName);
            var safe = FileNaming.Stem(fileName) + (extension.Length is > 1 and <= 6 && extension.Skip(1).All(char.IsLetterOrDigit) ? extension.ToLowerInvariant() : "");
            lock (_names)
                return System.IO.Path.Combine(OutputDirectory, FileNaming.Unique(safe, _names));
        }

        public string CreateScratchDirectory() =>
            Directory.CreateDirectory(System.IO.Path.Combine(ScratchDirectory, Interlocked.Increment(ref _scratchCount).ToString("D3", System.Globalization.CultureInfo.InvariantCulture))).FullName;
    }
}

/// <summary>Deletes expired jobs every minute.</summary>
public sealed class JobJanitor(DiskJobStore store, ILogger<JobJanitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var removed = store.RemoveExpired();
            if (removed > 0)
                logger.LogInformation("Deleted {Count} expired jobs", removed);
        }
    }
}
