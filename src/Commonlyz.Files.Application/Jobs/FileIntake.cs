using Commonlyz.Files.Application.Abstractions;
using Commonlyz.Files.Domain;
using Microsoft.Extensions.Options;

namespace Commonlyz.Files.Application.Jobs;

/// <summary>Stores uploads in the job workspace, enforcing count/size limits and detecting each file's real type.</summary>
public sealed class FileIntake(IFileTypeDetector detector, IOptions<ProcessingLimits> limits)
{
    public async Task<IReadOnlyList<InputFile>> StoreAsync(IReadOnlyList<IncomingFile> files, IJobWorkspace workspace, CancellationToken ct)
    {
        var l = limits.Value;
        InvalidInputException.ThrowIf(files.Count == 0, "Choose at least one file.");
        InvalidInputException.ThrowIf(files.Count > l.MaxFiles, $"At most {l.MaxFiles} files at a time.");
        InvalidInputException.ThrowIf(files.Sum(f => f.Length) > l.MaxTotalBytes, $"All files together must be at most {Mb(l.MaxTotalBytes)}.");

        var stored = new List<InputFile>(files.Count);
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            InvalidInputException.ThrowIf(file.Length == 0, $"{Display(file)} is empty.");
            InvalidInputException.ThrowIf(file.Length > l.MaxFileBytes, $"{Display(file)} is larger than {Mb(l.MaxFileBytes)}.");

            // Stored under a neutral name: the client's file name never touches the file system.
            var path = Path.Combine(workspace.InputDirectory, $"input-{i:D3}");
            long written;
            await using (var source = file.OpenRead())
            await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                written = await CopyLimitedAsync(source, target, l.MaxFileBytes, ct);
            InvalidInputException.ThrowIf(written > l.MaxFileBytes, $"{Display(file)} is larger than {Mb(l.MaxFileBytes)}.");

            var kind = detector.Detect(path);
            InvalidInputException.ThrowIf(kind == FileKind.Unknown,
                $"{Display(file)} is not a supported file. Use PDF, Word (DOCX, DOC, ODT, RTF) or images (JPEG, PNG, WebP, GIF, BMP, TIFF).");
            stored.Add(new InputFile(path, Path.GetFileName(file.FileName), kind, written));
        }
        return stored;
    }

    private static async Task<long> CopyLimitedAsync(Stream source, Stream target, long limit, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > limit)
                return total;
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return total;
    }

    private static string Display(IncomingFile file) => $"\"{FileNaming.Stem(file.FileName)}\"";

    private static string Mb(long bytes) => $"{bytes / (1024 * 1024)} MB";
}
