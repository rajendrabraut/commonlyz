using Kamala.Application.Abstractions;
using Kamala.Application.Jobs;
using Kamala.Domain;
using Kamala.Domain.Conversion;
using Kamala.Domain.Images;

namespace Kamala.Application.Conversion;

/// <summary>JPG/PNG/... to PDF: one merged PDF, or one PDF per image.</summary>
public sealed class ImagesToPdfRoute(IPdfComposer composer, JobRunner jobs) : IConversionRoute
{
    public FileCategory Source => FileCategory.Image;
    public IReadOnlySet<ConversionTarget> Targets => Conversion.Targets.Pdf;
    public bool IsAvailable(Capabilities c) => c.Documents;

    public async Task<IReadOnlyList<OutputFile>> ConvertAsync(IReadOnlyList<InputFile> inputs, ConversionRequest request, IJobWorkspace workspace, CancellationToken ct)
    {
        if (request.MergeIntoOne || inputs.Count == 1)
            return [await composer.ComposeFromImagesAsync(inputs, $"{inputs[0].Stem}.pdf", workspace, request, ct)];
        return await jobs.ForEachAsync(inputs, (input, token) => composer.ComposeFromImagesAsync([input], $"{input.Stem}.pdf", workspace, request, token), ct);
    }
}

/// <summary>Images to an editable Word document, one picture per page.</summary>
public sealed class ImagesToWordRoute(IWordComposer composer, JobRunner jobs) : IConversionRoute
{
    public FileCategory Source => FileCategory.Image;
    public IReadOnlySet<ConversionTarget> Targets => Conversion.Targets.Docx;
    public bool IsAvailable(Capabilities c) => c.Documents;

    public async Task<IReadOnlyList<OutputFile>> ConvertAsync(IReadOnlyList<InputFile> inputs, ConversionRequest request, IJobWorkspace workspace, CancellationToken ct)
    {
        if (request.MergeIntoOne || inputs.Count == 1)
            return [await composer.FromImagesAsync(inputs, $"{inputs[0].Stem}.docx", workspace, ct)];
        return await jobs.ForEachAsync(inputs, (input, token) => composer.FromImagesAsync([input], $"{input.Stem}.docx", workspace, token), ct);
    }
}

/// <summary>Image format conversion (e.g. PNG to JPG, WebP to PNG) through the image engine.</summary>
public sealed class ImageToImageRoute(IImageEditor editor, JobRunner jobs) : IConversionRoute
{
    public FileCategory Source => FileCategory.Image;
    public IReadOnlySet<ConversionTarget> Targets => Conversion.Targets.Images;
    public bool IsAvailable(Capabilities c) => c.ImageEditing;

    public async Task<IReadOnlyList<OutputFile>> ConvertAsync(IReadOnlyList<InputFile> inputs, ConversionRequest request, IJobWorkspace workspace, CancellationToken ct)
    {
        var format = request.Target switch
        {
            ConversionTarget.Jpeg => ImageFormat.Jpeg,
            ConversionTarget.Webp => ImageFormat.Webp,
            _ => ImageFormat.Png,
        };
        var edit = new ImageEditRequest([], format, request.Quality);
        return await jobs.ForEachAsync(inputs, async (input, token) =>
        {
            var path = workspace.ReserveOutputPath($"{input.Stem}.{format.Resolve(input.Kind).Extension()}");
            var outcome = await editor.EditAsync(input, path, edit, token);
            return new OutputFile(path, outcome.Format, outcome.Width, outcome.Height, input);
        }, ct);
    }
}

/// <summary>PDF pages to JPG/PNG/WebP images.</summary>
public sealed class PdfToImagesRoute(IPdfRasterizer rasterizer, JobRunner jobs) : IConversionRoute
{
    public FileCategory Source => FileCategory.Pdf;
    public IReadOnlySet<ConversionTarget> Targets => Conversion.Targets.Images;
    public bool IsAvailable(Capabilities c) => c.Documents;

    public async Task<IReadOnlyList<OutputFile>> ConvertAsync(IReadOnlyList<InputFile> inputs, ConversionRequest request, IJobWorkspace workspace, CancellationToken ct) =>
        (await jobs.ForEachAsync(inputs, (input, token) => rasterizer.RenderPagesAsync(input, workspace, request, token), ct)).SelectMany(p => p).ToList();
}

/// <summary>PDF to an editable Word document.</summary>
public sealed class PdfToWordRoute(IWordComposer composer, JobRunner jobs) : IConversionRoute
{
    public FileCategory Source => FileCategory.Pdf;
    public IReadOnlySet<ConversionTarget> Targets => Conversion.Targets.Docx;
    public bool IsAvailable(Capabilities c) => c.Documents;

    public async Task<IReadOnlyList<OutputFile>> ConvertAsync(IReadOnlyList<InputFile> inputs, ConversionRequest request, IJobWorkspace workspace, CancellationToken ct) =>
        await jobs.ForEachAsync(inputs, (input, token) => composer.FromPdfAsync(input, $"{input.Stem}.docx", workspace, token), ct);
}

/// <summary>Word (DOCX, DOC, ODT, RTF) to PDF.</summary>
public sealed class WordToPdfRoute(IOfficeConverter office, JobRunner jobs) : IConversionRoute
{
    public FileCategory Source => FileCategory.Word;
    public IReadOnlySet<ConversionTarget> Targets => Conversion.Targets.Pdf;
    public bool IsAvailable(Capabilities c) => c.WordDocuments;

    public async Task<IReadOnlyList<OutputFile>> ConvertAsync(IReadOnlyList<InputFile> inputs, ConversionRequest request, IJobWorkspace workspace, CancellationToken ct) =>
        await jobs.ForEachAsync(inputs, (input, token) => office.ConvertAsync(input, OfficeFormat.Pdf, workspace, token), ct);
}

/// <summary>Word pages to images: Word to PDF with LibreOffice, then the PDF pages to images.</summary>
public sealed class WordToImagesRoute(IOfficeConverter office, IPdfRasterizer rasterizer, JobRunner jobs) : IConversionRoute
{
    public FileCategory Source => FileCategory.Word;
    public IReadOnlySet<ConversionTarget> Targets => Conversion.Targets.Images;
    public bool IsAvailable(Capabilities c) => c.WordDocuments && c.Documents;

    public async Task<IReadOnlyList<OutputFile>> ConvertAsync(IReadOnlyList<InputFile> inputs, ConversionRequest request, IJobWorkspace workspace, CancellationToken ct)
    {
        var pages = await jobs.ForEachAsync(inputs, async (input, token) =>
        {
            var pdf = await office.ConvertAsync(input, OfficeFormat.Pdf, workspace, token);
            var intermediate = new InputFile(pdf.Path, input.OriginalName, FileKind.Pdf, new FileInfo(pdf.Path).Length);
            var images = await rasterizer.RenderPagesAsync(intermediate, workspace, request, token);
            File.Delete(pdf.Path); // only the images are the result
            return images;
        }, ct);
        return pages.SelectMany(p => p).ToList();
    }
}

/// <summary>Older Word formats (DOC, ODT, RTF) to DOCX.</summary>
public sealed class WordToDocxRoute(IOfficeConverter office, JobRunner jobs) : IConversionRoute
{
    public FileCategory Source => FileCategory.Word;
    public IReadOnlySet<ConversionTarget> Targets => Conversion.Targets.Docx;
    public bool IsAvailable(Capabilities c) => c.WordDocuments;

    public async Task<IReadOnlyList<OutputFile>> ConvertAsync(IReadOnlyList<InputFile> inputs, ConversionRequest request, IJobWorkspace workspace, CancellationToken ct)
    {
        InvalidInputException.ThrowIf(inputs.Any(i => i.Kind == FileKind.Docx), "The file is already a DOCX document.");
        return await jobs.ForEachAsync(inputs, (input, token) => office.ConvertAsync(input, OfficeFormat.Docx, workspace, token), ct);
    }
}
