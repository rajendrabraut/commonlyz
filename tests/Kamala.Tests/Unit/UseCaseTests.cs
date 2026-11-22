using Kamala.Application;
using Kamala.Application.Abstractions;
using Kamala.Application.Conversion;
using Kamala.Application.Jobs;
using Kamala.Domain;
using Kamala.Domain.Conversion;
using Kamala.Domain.Images;
using Kamala.Infrastructure.Detection;
using Kamala.Infrastructure;
using Kamala.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace Kamala.Tests.Unit;

/// <summary>Use cases with fake engines: the ports make the application layer testable without any engine installed.</summary>
public sealed class UseCaseTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("files-usecases-").FullName;
    private readonly FakeEngines _engines = new();
    private readonly JobRunner _jobs;
    private readonly IJobStore _store;

    public UseCaseTests()
    {
        _store = new DiskJobStore(Options.Create(new StorageOptions { WorkDirectory = Path.Combine(_dir, "work") }), TimeProvider.System);
        var limits = Options.Create(new ProcessingLimits { MaxFiles = 3, MaxFileBytes = 1024 * 1024 });
        _jobs = new JobRunner(_store, new FileIntake(new MagicByteFileTypeDetector(), limits), limits);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static IncomingFile File(string name, byte[] bytes) => new(name, bytes.Length, () => new MemoryStream(bytes));

    private ConvertFilesUseCase Converter(Capabilities? capabilities = null) => new(_jobs,
    [
        new ImagesToPdfRoute(_engines, _jobs), new ImagesToWordRoute(_engines, _jobs), new ImageToImageRoute(_engines, _jobs),
        new PdfToImagesRoute(_engines, _jobs), new PdfToWordRoute(_engines, _jobs), new WordToPdfRoute(_engines, _jobs),
        new WordToImagesRoute(_engines, _engines, _jobs), new WordToDocxRoute(_engines, _jobs),
    ], new FixedCapabilities(capabilities ?? new Capabilities(true, true, true, true)));

    [Fact]
    public async Task Images_merge_into_one_pdf()
    {
        var result = await Converter().ExecuteAsync([File("a.png", TestFiles.Png(8, 8)), File("b.png", TestFiles.Png(8, 8))],
            new ConversionRequest(ConversionTarget.Pdf), CancellationToken.None);
        result.Files.Single().Name.ShouldBe("a.pdf");
        _engines.Calls.ShouldBe(["compose:2"]);
    }

    [Fact]
    public async Task Images_to_separate_pdfs_when_not_merged()
    {
        var result = await Converter().ExecuteAsync([File("a.png", TestFiles.Png(8, 8)), File("b.png", TestFiles.Png(8, 8))],
            new ConversionRequest(ConversionTarget.Pdf, MergeIntoOne: false), CancellationToken.None);
        result.Files.Select(f => f.Name).Order().ShouldBe(["a.pdf", "b.pdf"]);
    }

    [Fact]
    public async Task Word_to_images_goes_through_pdf()
    {
        var result = await Converter().ExecuteAsync([File("letter.docx", TestFiles.Docx("hi"))], new ConversionRequest(ConversionTarget.Png), CancellationToken.None);
        _engines.Calls.ShouldBe(["office:Pdf", "render:Pdf"]);
        result.Files.Single().Name.ShouldBe("letter-001.png");
    }

    [Fact]
    public async Task Mixed_inputs_are_rejected()
    {
        var ex = await Should.ThrowAsync<InvalidInputException>(() => Converter().ExecuteAsync(
            [File("a.png", TestFiles.Png(8, 8)), File("b.pdf", TestFiles.TextPdf("x"))], new ConversionRequest(ConversionTarget.Docx), CancellationToken.None));
        ex.Message.ShouldContain("one kind of file");
    }

    [Fact]
    public async Task Unsupported_pairs_are_rejected()
    {
        var ex = await Should.ThrowAsync<InvalidInputException>(() => Converter().ExecuteAsync(
            [File("a.pdf", TestFiles.TextPdf("x"))], new ConversionRequest(ConversionTarget.Pdf), CancellationToken.None));
        ex.Message.ShouldContain("not supported");
    }

    [Fact]
    public async Task Missing_engine_reports_unavailable()
    {
        await Should.ThrowAsync<FeatureUnavailableException>(() => Converter(new Capabilities(true, true, false, true)).ExecuteAsync(
            [File("a.docx", TestFiles.Docx("x"))], new ConversionRequest(ConversionTarget.Pdf), CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_files_and_limits_are_enforced()
    {
        var convert = Converter();
        (await Should.ThrowAsync<InvalidInputException>(() => convert.ExecuteAsync([File("x.pdf", "MZ evil"u8.ToArray())],
            new ConversionRequest(ConversionTarget.Docx), CancellationToken.None))).Message.ShouldContain("not a supported file");
        (await Should.ThrowAsync<InvalidInputException>(() => convert.ExecuteAsync(
            Enumerable.Range(0, 4).Select(i => File($"{i}.png", TestFiles.Png(2, 2))).ToList(), new ConversionRequest(ConversionTarget.Pdf), CancellationToken.None)))
            .Message.ShouldContain("At most 3");
        (await Should.ThrowAsync<InvalidInputException>(() => convert.ExecuteAsync([File("big.png", new byte[2 * 1024 * 1024])],
            new ConversionRequest(ConversionTarget.Pdf), CancellationToken.None))).Message.ShouldContain("larger than");
        (await Should.ThrowAsync<InvalidInputException>(() => convert.ExecuteAsync([],
            new ConversionRequest(ConversionTarget.Pdf), CancellationToken.None))).Message.ShouldContain("at least one");
    }

    [Fact]
    public async Task Failed_jobs_leave_nothing_behind()
    {
        _engines.Fail = true;
        await Should.ThrowAsync<ProcessingFailedException>(() => Converter().ExecuteAsync([File("a.pdf", TestFiles.TextPdf("x"))],
            new ConversionRequest(ConversionTarget.Docx), CancellationToken.None));
        Directory.GetDirectories(Path.Combine(_dir, "work")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Edit_rejects_non_images()
    {
        var edit = new EditImagesUseCase(_jobs, _engines, new FixedCapabilities(new Capabilities(true, true, true, true)));
        await Should.ThrowAsync<InvalidInputException>(() => edit.ExecuteAsync([File("a.pdf", TestFiles.TextPdf("x"))],
            new ImageEditRequest([]), CancellationToken.None));
    }

    [Fact]
    public void Conversion_options_reflect_capabilities()
    {
        var options = Converter(new Capabilities(true, true, false, false)).Options();
        options.Single(o => o.Source == FileCategory.Word && o.Target == ConversionTarget.Pdf).Available.ShouldBeFalse();
        options.Single(o => o.Source == FileCategory.Pdf && o.Target == ConversionTarget.Docx).Available.ShouldBeTrue();
        options.Count.ShouldBe(14);
    }

    private sealed class FixedCapabilities(Capabilities capabilities) : ICapabilityProvider
    {
        public Capabilities Current => capabilities;
    }

    /// <summary>Records calls and writes placeholder outputs.</summary>
    private sealed class FakeEngines : IImageEditor, IPdfRasterizer, IPdfComposer, IWordComposer, IOfficeConverter
    {
        public List<string> Calls { get; } = [];
        public bool Fail { get; set; }

        private OutputFile Make(IJobWorkspace ws, string name, FileKind kind, string call)
        {
            if (Fail)
                throw new ProcessingFailedException("boom");
            lock (Calls)
                Calls.Add(call);
            var path = ws.ReserveOutputPath(name);
            System.IO.File.WriteAllText(path, "x");
            return new OutputFile(path, kind);
        }

        public Task<ImageEditOutcome> EditAsync(InputFile input, string outputPath, ImageEditRequest request, CancellationToken ct)
        {
            System.IO.File.WriteAllText(outputPath, "x");
            return Task.FromResult(new ImageEditOutcome(1, 1, FileKind.Png));
        }

        public Task<IReadOnlyList<OutputFile>> RenderPagesAsync(InputFile pdf, IJobWorkspace ws, ConversionRequest request, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<OutputFile>>([Make(ws, $"{pdf.Stem}-001.png", FileKind.Png, $"render:{pdf.Kind}")]);

        public Task<OutputFile> ComposeFromImagesAsync(IReadOnlyList<InputFile> images, string outputName, IJobWorkspace ws, ConversionRequest request, CancellationToken ct) =>
            Task.FromResult(Make(ws, outputName, FileKind.Pdf, $"compose:{images.Count}"));

        public Task<OutputFile> FromPdfAsync(InputFile pdf, string outputName, IJobWorkspace ws, CancellationToken ct) =>
            Task.FromResult(Make(ws, outputName, FileKind.Docx, "pdf2docx"));

        public Task<OutputFile> FromImagesAsync(IReadOnlyList<InputFile> images, string outputName, IJobWorkspace ws, CancellationToken ct) =>
            Task.FromResult(Make(ws, outputName, FileKind.Docx, "img2docx"));

        public Task<OutputFile> ConvertAsync(InputFile document, OfficeFormat target, IJobWorkspace ws, CancellationToken ct) =>
            Task.FromResult(Make(ws, $"{document.Stem}.{target.ToString().ToLowerInvariant()}", target == OfficeFormat.Pdf ? FileKind.Pdf : FileKind.Docx, $"office:{target}"));
    }
}
