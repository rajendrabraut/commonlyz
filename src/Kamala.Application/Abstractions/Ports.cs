using Kamala.Domain;
using Kamala.Domain.Conversion;
using Kamala.Domain.Images;

namespace Kamala.Application.Abstractions;

// Ports implemented by Infrastructure. Each is small and focused (interface segregation), so an engine can be
// swapped (e.g. a different PDF renderer) without touching use cases.

public interface IFileTypeDetector
{
    /// <summary>Returns the real type from the file's bytes; throws <see cref="InvalidInputException"/> for unsafe archives.</summary>
    FileKind Detect(string path);
}

/// <summary>A private scratch area for one job: uploads in, results out. Deleted when the job expires.</summary>
public interface IJobWorkspace
{
    string JobId { get; }
    string InputDirectory { get; }
    string OutputDirectory { get; }
    string ScratchDirectory { get; }

    /// <summary>Returns a free path in the output directory for a sanitized, de-duplicated file name.</summary>
    string ReserveOutputPath(string fileName);

    /// <summary>Returns a new empty directory under the scratch area.</summary>
    string CreateScratchDirectory();
}

public interface IJobStore
{
    IJobWorkspace Create();

    /// <summary>Makes the outputs downloadable under unguessable ids until the retention period ends.</summary>
    JobResult Publish(IJobWorkspace workspace, IReadOnlyList<OutputFile> outputs);

    /// <summary>Removes a job that failed, so its files don't wait for expiry.</summary>
    void Discard(IJobWorkspace workspace);

    StoredFile? FindFile(string fileId);

    IReadOnlyList<StoredFile> FindJobFiles(string jobId);
}

public sealed record ImageEditOutcome(int Width, int Height, FileKind Format);

public interface IImageEditor
{
    Task<ImageEditOutcome> EditAsync(InputFile input, string outputPath, ImageEditRequest request, CancellationToken ct);
}

public interface IImageUpscaler
{
    Task<OutputFile> UpscaleAsync(InputFile input, IJobWorkspace workspace, UpscaleRequest request, CancellationToken ct);
}

public interface IPdfRasterizer
{
    Task<IReadOnlyList<OutputFile>> RenderPagesAsync(InputFile pdf, IJobWorkspace workspace, ConversionRequest request, CancellationToken ct);
}

public interface IPdfComposer
{
    Task<OutputFile> ComposeFromImagesAsync(IReadOnlyList<InputFile> images, string outputName, IJobWorkspace workspace, ConversionRequest request, CancellationToken ct);
}

public interface IWordComposer
{
    Task<OutputFile> FromPdfAsync(InputFile pdf, string outputName, IJobWorkspace workspace, CancellationToken ct);

    Task<OutputFile> FromImagesAsync(IReadOnlyList<InputFile> images, string outputName, IJobWorkspace workspace, CancellationToken ct);
}

public enum OfficeFormat
{
    Pdf,
    Docx,
}

/// <summary>Reads Word-family documents (DOCX, DOC, ODT, RTF) and writes PDF or DOCX.</summary>
public interface IOfficeConverter
{
    Task<OutputFile> ConvertAsync(InputFile document, OfficeFormat target, IJobWorkspace workspace, CancellationToken ct);
}

/// <summary>Which features this server can offer, based on the engines that are installed.</summary>
public sealed record Capabilities(bool ImageEditing, bool Documents, bool WordDocuments, bool AiUpscale);

public interface ICapabilityProvider
{
    Capabilities Current { get; }
}
