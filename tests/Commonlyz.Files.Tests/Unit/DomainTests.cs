using Commonlyz.Files.Application;
using Commonlyz.Files.Domain;
using Commonlyz.Files.Domain.Conversion;
using Commonlyz.Files.Domain.Images;

namespace Commonlyz.Files.Tests.Unit;

public sealed class DomainTests
{
    [Theory]
    [InlineData("1", new[] { 1, 1 })]
    [InlineData("1-3, 5", new[] { 1, 3, 5, 5 })]
    [InlineData("2-2,4-6", new[] { 2, 2, 4, 6 })]
    public void Page_ranges_parse(string spec, int[] bounds)
    {
        var ranges = PageRange.Parse(spec);
        ranges.SelectMany(r => new[] { r.From, r.To }).ShouldBe(bounds);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("3-1")]
    [InlineData("a-b")]
    [InlineData("1-2-3")]
    [InlineData(",")]
    [InlineData("99999")]
    public void Bad_page_ranges_are_rejected(string spec) =>
        Should.Throw<InvalidInputException>(() => PageRange.Parse(spec));

    public static TheoryData<ImageOperation> InvalidOperations() =>
    [
        new CropOperation(0, 0, 0, 10),
        new CropOperation(-1, 0, 10, 10),
        new CropToRatioOperation(new AspectRatio(0, 1)),
        new PadToRatioOperation(new AspectRatio(1, 1), "red"),
        new ResizeOperation(null, null),
        new ResizeOperation(30_000, null),
        new ScaleOperation(0),
        new ScaleOperation(double.NaN),
        new RotateOperation(45),
        new AdjustOperation(150, 0, 0),
        new SharpenOperation(9, 1),
        new DenoiseOperation(4),
        new BlurOperation(50),
    ];

    [Theory]
    [MemberData(nameof(InvalidOperations))]
    public void Invalid_operations_are_rejected(ImageOperation operation) =>
        Should.Throw<InvalidInputException>(() => new ImageEditRequest([operation]).Validate());

    [Fact]
    public void Valid_edit_passes()
    {
        new ImageEditRequest(
        [
            new CropOperation(0, 0, 10, 10), new CropToRatioOperation(new AspectRatio(16, 9)), new PadToRatioOperation(new AspectRatio(1, 1)),
            new ResizeOperation(800, null), new ScaleOperation(50), new RotateOperation(-90), new FlipOperation(FlipAxis.Vertical),
            new AdjustOperation(10, -10, 20), new SharpenOperation(), new DenoiseOperation(2), new AutoEnhanceOperation(), new GrayscaleOperation(),
            new BlurOperation(1),
        ], ImageFormat.Jpeg, 70, TargetBytes: 50_000).Validate();
    }

    [Fact]
    public void Target_size_needs_jpeg() =>
        Should.Throw<InvalidInputException>(() => new ImageEditRequest([], ImageFormat.Png, TargetBytes: 50_000).Validate());

    [Fact]
    public void Too_many_steps_are_rejected() =>
        Should.Throw<InvalidInputException>(() => new ImageEditRequest(Enumerable.Repeat<ImageOperation>(new GrayscaleOperation(), 21).ToList()).Validate());

    [Theory]
    [InlineData(ImageFormat.Same, FileKind.Jpeg, FileKind.Jpeg)]
    [InlineData(ImageFormat.Same, FileKind.Webp, FileKind.Webp)]
    [InlineData(ImageFormat.Same, FileKind.Gif, FileKind.Png)]
    [InlineData(ImageFormat.Same, FileKind.Tiff, FileKind.Png)]
    [InlineData(ImageFormat.Webp, FileKind.Png, FileKind.Webp)]
    public void Same_format_resolves(ImageFormat format, FileKind input, FileKind expected) =>
        format.Resolve(input).ShouldBe(expected);

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\Windows\\system32\\evil.exe", "evil")]
    [InlineData("report<script>.pdf", "report_script_")]
    [InlineData("   .pdf", "file")]
    [InlineData("holiday photo (1).jpg", "holiday photo (1)")]
    public void File_names_are_sanitized(string input, string stem) => FileNaming.Stem(input).ShouldBe(stem);

    [Fact]
    public void Duplicate_names_get_numbered()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        FileNaming.Unique("a.jpg", used).ShouldBe("a.jpg");
        FileNaming.Unique("A.jpg", used).ShouldBe("A-2.jpg");
        FileNaming.Unique("a.jpg", used).ShouldBe("a-3.jpg");
    }

    [Fact]
    public void Conversion_request_validates()
    {
        new ConversionRequest(ConversionTarget.Pdf).Validate();
        Should.Throw<InvalidInputException>(() => new ConversionRequest(ConversionTarget.Jpeg, Dpi: 5000).Validate());
        Should.Throw<InvalidInputException>(() => new ConversionRequest(ConversionTarget.Jpeg, Pages: "x").Validate());
        Should.Throw<InvalidInputException>(() => new UpscaleRequest(8).Validate());
    }
}
