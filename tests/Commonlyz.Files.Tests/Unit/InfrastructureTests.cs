using System.IO.Compression;
using Commonlyz.Files.Domain;
using Commonlyz.Files.Domain.Images;
using Commonlyz.Files.Infrastructure;
using Commonlyz.Files.Infrastructure.Detection;
using Commonlyz.Files.Infrastructure.Engines;
using Commonlyz.Files.Infrastructure.Storage;
using Commonlyz.Files.Application.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Commonlyz.Files.Tests.Unit;

public sealed class InfrastructureTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("files-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(byte[] bytes, string name = "f")
    {
        var path = Path.Combine(_dir, name + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Detector_uses_content_not_names()
    {
        var detector = new MagicByteFileTypeDetector();
        detector.Detect(Write(TestFiles.Png(4, 4), "x.pdf")).ShouldBe(FileKind.Png);
        detector.Detect(Write(TestFiles.TextPdf("hi"), "x.jpg")).ShouldBe(FileKind.Pdf);
        detector.Detect(Write(TestFiles.Docx("hi"))).ShouldBe(FileKind.Docx);
        detector.Detect(Write([0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10])).ShouldBe(FileKind.Jpeg);
        detector.Detect(Write("{\\rtf1 hello}"u8.ToArray())).ShouldBe(FileKind.Rtf);
        detector.Detect(Write("MZ\x90\0 not a document"u8.ToArray())).ShouldBe(FileKind.Unknown);
        detector.Detect(Write([])).ShouldBe(FileKind.Unknown);
    }

    [Fact]
    public void Detector_recognises_odt_and_plain_zip()
    {
        var detector = new MagicByteFileTypeDetector();
        detector.Detect(Write(Zip(("mimetype", "application/vnd.oasis.opendocument.text"u8.ToArray()), ("content.xml", "<x/>"u8.ToArray())))).ShouldBe(FileKind.Odt);
        detector.Detect(Write(Zip(("readme.txt", "hello"u8.ToArray())))).ShouldBe(FileKind.Unknown);
    }

    [Fact]
    public void Detector_rejects_zip_bombs()
    {
        // 64 MB of zeros compresses to a few KB: far above the allowed expansion ratio.
        var bomb = Zip(("word/document.xml", new byte[64 * 1024 * 1024]));
        Should.Throw<InvalidInputException>(() => new MagicByteFileTypeDetector().Detect(Write(bomb)))
            .Message.ShouldContain("zip bomb");
    }

    [Fact]
    public void Every_operation_maps_to_imgcore_json()
    {
        ImageOperation[] all =
        [
            new CropOperation(1, 2, 3, 4), new CropToRatioOperation(new AspectRatio(16, 9)), new PadToRatioOperation(new AspectRatio(1, 1), "#000000"),
            new ResizeOperation(100, null, ResizeFit.Cover, false), new ScaleOperation(50), new RotateOperation(90), new FlipOperation(FlipAxis.Horizontal),
            new AdjustOperation(1, 2, 3), new SharpenOperation(), new DenoiseOperation(), new AutoEnhanceOperation(), new GrayscaleOperation(), new BlurOperation(2),
        ];
        var json = all.Select(o => RustImageEditor.ToJson(o).ToJsonString()).ToList();
        json[0].ShouldBe("""{"op":"crop","x":1,"y":2,"width":3,"height":4}""");
        json[3].ShouldBe("""{"op":"resize","width":100,"height":null,"fit":"cover","upscale":false}""");
        json[6].ShouldBe("""{"op":"flip","axis":"horizontal"}""");
        json.Select(j => j.Split('"')[3]).ShouldBe(["crop", "crop_ratio", "pad_ratio", "resize", "scale", "rotate", "flip", "adjust", "sharpen", "denoise", "auto_enhance", "grayscale", "blur"]);
    }

    [Fact]
    public void Job_store_publishes_under_random_ids_and_expires()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new DiskJobStore(Options.Create(new StorageOptions { WorkDirectory = Path.Combine(_dir, "work"), Retention = TimeSpan.FromMinutes(30) }), clock);
        var job = store.Create();
        var path = job.ReserveOutputPath("../../evil name.PDF");
        Path.GetDirectoryName(path).ShouldBe(job.OutputDirectory);
        Path.GetFileName(path).ShouldBe("evil name.pdf");
        File.WriteAllText(path, "%PDF-");

        var result = store.Publish(job, [new OutputFile(path, FileKind.Pdf)]);
        result.Files.Single().Id.Length.ShouldBe(32);
        store.FindFile(result.Files[0].Id).ShouldNotBeNull().ContentType.ShouldBe("application/pdf");
        store.FindJobFiles(result.JobId).Count.ShouldBe(1);

        clock.Advance(TimeSpan.FromMinutes(31));
        store.FindFile(result.Files[0].Id).ShouldBeNull();
        store.RemoveExpired().ShouldBe(1);
        Directory.Exists(Path.Combine(_dir, "work", result.JobId)).ShouldBeFalse();
    }

    [Fact]
    public void Job_store_refuses_to_wipe_a_foreign_folder()
    {
        var foreign = Directory.CreateDirectory(Path.Combine(_dir, "precious")).FullName;
        File.WriteAllText(Path.Combine(foreign, "thesis.docx"), "do not delete");
        Should.Throw<InvalidOperationException>(() =>
            new DiskJobStore(Options.Create(new StorageOptions { WorkDirectory = foreign }), TimeProvider.System));
        File.Exists(Path.Combine(foreign, "thesis.docx")).ShouldBeTrue();
    }

    [Fact]
    public void Reserved_output_names_are_unique()
    {
        var store = new DiskJobStore(Options.Create(new StorageOptions { WorkDirectory = Path.Combine(_dir, "work2") }), TimeProvider.System);
        var job = store.Create();
        Path.GetFileName(job.ReserveOutputPath("a.jpg")).ShouldBe("a.jpg");
        Path.GetFileName(job.ReserveOutputPath("a.jpg")).ShouldBe("a-2.jpg");
        // The unsafe "extension" is dropped rather than kept.
        Path.GetFileName(job.ReserveOutputPath("a.exe;rm -rf")).ShouldBe("a");
    }

    private static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var entry = zip.CreateEntry(name, CompressionLevel.SmallestSize).Open();
                entry.Write(content);
            }
        }
        return stream.ToArray();
    }
}
