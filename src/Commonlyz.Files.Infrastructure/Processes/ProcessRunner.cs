using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Commonlyz.Files.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Commonlyz.Files.Infrastructure.Processes;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// Runs engine processes safely: arguments are passed as a list (never through a shell), the environment is
/// stripped to the minimum (no connection strings or secrets reach engines), output is size-capped, a timeout
/// kills the whole process tree, and a global gate caps how many engines run at once.
/// </summary>
public sealed class ProcessRunner(IOptions<EngineOptions> options, ILogger<ProcessRunner> logger) : IDisposable
{
    private const int MaxOutputChars = 1024 * 1024;

    private static readonly HashSet<string> PassThrough = new(StringComparer.OrdinalIgnoreCase)
    {
        "SystemRoot", "WINDIR", "PATH", "PATHEXT", "LANG", "LC_ALL", "TZ", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "ProgramFiles", "ProgramData",
    };

    private readonly SemaphoreSlim _gate = new(Math.Max(1, options.Value.MaxConcurrentProcesses));

    public async Task<ProcessResult> RunAsync(
        string fileName, IReadOnlyList<string> arguments, string? stdin, string workingDirectory, TimeSpan timeout, CancellationToken ct)
    {
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        var kept = info.Environment.Where(e => PassThrough.Contains(e.Key)).ToList();
        info.Environment.Clear();
        foreach (var (key, value) in kept)
            info.Environment[key] = value;
        // Temp files and any profile data stay inside the job's own folder.
        foreach (var key in new[] { "TEMP", "TMP", "TMPDIR", "HOME", "USERPROFILE" })
            info.Environment[key] = workingDirectory;

        await _gate.WaitAsync(ct);
        try
        {
            return await RunCoreAsync(info, stdin, timeout, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ProcessResult> RunCoreAsync(ProcessStartInfo info, string? stdin, TimeSpan timeout, CancellationToken ct)
    {
        var engine = Path.GetFileNameWithoutExtension(info.FileName);
        using var process = new Process { StartInfo = info };
        var started = Stopwatch.GetTimestamp();
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new ProcessingFailedException($"Could not start the {engine} engine.", ex);
        }

        var stdout = ReadCappedAsync(process.StandardOutput);
        var stderr = ReadCappedAsync(process.StandardError);
        if (stdin is not null)
            await process.StandardInput.WriteAsync(stdin.AsMemory(), ct);
        process.StandardInput.Close();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (ct.IsCancellationRequested)
                throw;
            logger.LogWarning("{Engine} timed out after {Timeout}", engine, timeout);
            throw new InvalidInputException($"Processing took longer than {timeout.TotalSeconds:0} seconds and was stopped. Try a smaller file.");
        }

        var result = new ProcessResult(process.ExitCode, await stdout, await stderr);
        logger.LogInformation("{Engine} exited {ExitCode} in {Elapsed} ms", engine, result.ExitCode, (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return result;
    }

    /// <summary>Parses an engine's JSON reply line. Exit code 2 means a user error whose message is safe to show.</summary>
    public JsonElement ParseReply(ProcessResult result, string engine)
    {
        var line = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        JsonElement reply = default;
        if (line is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                reply = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Not JSON: handled as an engine failure below.
            }
        }

        if (result.ExitCode == 0 && reply.ValueKind == JsonValueKind.Object)
            return reply;

        var message = reply.ValueKind == JsonValueKind.Object && reply.TryGetProperty("error", out var e) ? e.GetString() : null;
        if (result.ExitCode == 2 && !string.IsNullOrWhiteSpace(message))
            throw new InvalidInputException(message);

        logger.LogError("{Engine} failed with exit code {ExitCode}: {Message} {StdErr}", engine, result.ExitCode, message, Truncate(result.StdErr, 4000));
        throw new ProcessingFailedException($"The {engine} engine failed.");
    }

    public void Dispose() => _gate.Dispose();

    private static async Task<string> ReadCappedAsync(StreamReader reader)
    {
        var builder = new StringBuilder();
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            if (builder.Length < MaxOutputChars)
                builder.Append(buffer, 0, Math.Min(read, MaxOutputChars - builder.Length));
        }
        return builder.ToString();
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
