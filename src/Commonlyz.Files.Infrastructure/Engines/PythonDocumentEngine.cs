using System.Text.Json;
using System.Text.Json.Nodes;
using Commonlyz.Files.Application;
using Commonlyz.Files.Application.Abstractions;
using Commonlyz.Files.Domain;
using Commonlyz.Files.Domain.Conversion;
using Commonlyz.Files.Domain.Images;
using Commonlyz.Files.Infrastructure.Processes;
using Microsoft.Extensions.Options;

namespace Commonlyz.Files.Infrastructure.Engines;

/// <summary>
/// Talks to the Python docengine: one isolated process per job (<c>-I</c> ignores PYTHON* variables and the user
/// site directory, <c>-B</c> writes no bytecode). The engine writes into a private scratch folder; results are then
/// moved into the job's output folder under safe names.
/// </summary>
public sealed class DocEngineClient(ProcessRunner runner, IOptions<EngineOptions> engines, IOptions<ProcessingLimits> limits)
{
    public async Task<IReadOnlyList<OutputFile>> RunAsync(string command, JsonObject job, IJobWorkspace workspace, FileKind kind, InputFile? source, CancellationToken ct)
    {
        var o = engines.Value;
        var scratch = workspace.CreateScratchDirectory();
        job["command"] = command;
        job["output_dir"] = scratch;
        job["max_pixels"] = limits.Value.MaxPixels;
        job["max_pages"] = limits.Value.MaxPdfPages;

        // -I drops the working directory from sys.path, so the package directory is added explicitly.
        var bootstrap = $"import sys; sys.path.insert(0, {JsonSerializer.Serialize(o.DocEngineDirectory)}); " +
                        "from docengine.__main__ import main; sys.exit(main())";
        var result = await runner.RunAsync(o.PythonPath!, ["-I", "-B", "-c", bootstrap], job.ToJsonString(), scratch, o.Timeout, ct);
        var reply = runner.ParseReply(result, "document");

        var outputs = new List<OutputFile>();
        foreach (var file in reply.GetProperty("files").EnumerateArray())
        {
            var name = file.GetProperty("name").GetString()!;
            var produced = Path.Combine(scratch, Path.GetFileName(name));
            var destination = workspace.ReserveOutputPath(name);
            File.Move(produced, destination, overwrite: true);
            outputs.Add(new OutputFile(destination, kind,
                file.TryGetProperty("width", out var w) ? w.GetInt32() : null,
                file.TryGetProperty("height", out var h) ? h.GetInt32() : null,
                source));
        }
        return outputs;
    }

    public static JsonArray Paths(IEnumerable<InputFile> inputs) => new(inputs.Select(i => (JsonNode)i.Path).ToArray());

    public static string ImageFormatName(ConversionTarget target) => target switch
    {
        ConversionTarget.Png => "png",
        ConversionTarget.Webp => "webp",
        _ => "jpeg",
    };

    public static FileKind ImageKind(ConversionTarget target) => target switch
    {
        ConversionTarget.Png => FileKind.Png,
        ConversionTarget.Webp => FileKind.Webp,
        _ => FileKind.Jpeg,
    };
}

/// <summary>PDF pages to images with Google's PDFium (pypdfium2).</summary>
public sealed class PythonPdfRasterizer(DocEngineClient engine) : IPdfRasterizer
{
    public Task<IReadOnlyList<OutputFile>> RenderPagesAsync(InputFile pdf, IJobWorkspace workspace, ConversionRequest request, CancellationToken ct) =>
        engine.RunAsync("pdf_to_images", new JsonObject
        {
            ["input"] = pdf.Path,
            ["format"] = DocEngineClient.ImageFormatName(request.Target),
            ["dpi"] = request.Dpi,
            ["quality"] = request.Quality,
            ["pages"] = request.Pages,
            ["stem"] = pdf.Stem,
        }, workspace, DocEngineClient.ImageKind(request.Target), pdf, ct);
}

/// <summary>Images to PDF; JPEGs are embedded without re-compression.</summary>
public sealed class PythonPdfComposer(DocEngineClient engine) : IPdfComposer
{
    public async Task<OutputFile> ComposeFromImagesAsync(
        IReadOnlyList<InputFile> images, string outputName, IJobWorkspace workspace, ConversionRequest request, CancellationToken ct) =>
        (await engine.RunAsync("images_to_pdf", new JsonObject
        {
            ["inputs"] = DocEngineClient.Paths(images),
            ["page_size"] = request.PageSize.ToString().ToLowerInvariant(),
            ["margin_pt"] = request.MarginPoints,
            ["output_name"] = outputName,
        }, workspace, FileKind.Pdf, images.Count == 1 ? images[0] : null, ct))[0];
}

/// <summary>Editable DOCX from PDFs (pdfminer layout analysis) or from images.</summary>
public sealed class PythonWordComposer(DocEngineClient engine) : IWordComposer
{
    public async Task<OutputFile> FromPdfAsync(InputFile pdf, string outputName, IJobWorkspace workspace, CancellationToken ct) =>
        (await engine.RunAsync("pdf_to_docx", new JsonObject { ["input"] = pdf.Path, ["output_name"] = outputName },
            workspace, FileKind.Docx, pdf, ct))[0];

    public async Task<OutputFile> FromImagesAsync(IReadOnlyList<InputFile> images, string outputName, IJobWorkspace workspace, CancellationToken ct) =>
        (await engine.RunAsync("images_to_docx", new JsonObject { ["inputs"] = DocEngineClient.Paths(images), ["output_name"] = outputName },
            workspace, FileKind.Docx, images.Count == 1 ? images[0] : null, ct))[0];
}

/// <summary>Real-ESRGAN AI upscaling on CPU through ONNX Runtime.</summary>
public sealed class PythonImageUpscaler(DocEngineClient engine, IOptions<EngineOptions> engines, IOptions<ProcessingLimits> limits) : IImageUpscaler
{
    public async Task<OutputFile> UpscaleAsync(InputFile input, IJobWorkspace workspace, UpscaleRequest request, CancellationToken ct)
    {
        var kind = request.Format.Resolve(input.Kind);
        var format = kind switch { FileKind.Jpeg => "jpeg", FileKind.Webp => "webp", _ => "png" };
        return (await engine.RunAsync("upscale", new JsonObject
        {
            ["input"] = input.Path,
            ["scale"] = request.Scale,
            ["format"] = format,
            ["quality"] = request.Quality,
            ["output_name"] = $"{input.Stem}-{request.Scale}x.{kind.Extension()}",
            ["model_path"] = engines.Value.UpscaleModelPath,
            ["max_input_pixels"] = limits.Value.MaxUpscalePixels,
        }, workspace, kind, input, ct))[0];
    }
}
