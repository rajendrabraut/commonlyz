using Commonlyz.Core;
using Commonlyz.Modules.FormFlow;
using Commonlyz.Modules.FormFlow.Data;
using Commonlyz.Modules.PixelSqueeze;
using Commonlyz.Modules.SlotBook;
using Commonlyz.Modules.SlotBook.Data;
using SkiaSharp;

namespace Commonlyz.Tests.Unit;

public class SlotGeneratorTests
{
    private static readonly MeetingType Type = new()
    {
        Slug = "x", Name = "Call", DurationMinutes = 30, BufferMinutes = 0, MinNoticeMinutes = 60, MaxDaysAhead = 30, TimeZoneId = "Europe/London",
    };

    private static readonly AvailabilityRule[] Monday9To11 = [new() { DayOfWeek = DayOfWeek.Monday, StartTime = new(9, 0), EndTime = new(11, 0) }];

    [Fact]
    public void Generates_slots_in_local_time_across_DST()
    {
        var now = new DateTime(2026, 3, 20, 0, 0, 0, DateTimeKind.Utc);
        // Monday 23 March (GMT, UTC+0) and Monday 30 March (BST, UTC+1)
        var slots = SlotGenerator.Generate(Type, Monday9To11, [], new DateOnly(2026, 3, 23), 8, now);

        slots.Count.ShouldBe(8);
        slots[0].StartUtc.ShouldBe(new DateTime(2026, 3, 23, 9, 0, 0, DateTimeKind.Utc));
        slots[4].StartUtc.ShouldBe(new DateTime(2026, 3, 30, 8, 0, 0, DateTimeKind.Utc));   // 09:00 BST
    }

    [Fact]
    public void Excludes_booked_slots_buffer_and_minimum_notice()
    {
        var type = new MeetingType { Slug = "x", Name = "Call", DurationMinutes = 30, BufferMinutes = 15, MinNoticeMinutes = 60, MaxDaysAhead = 30, TimeZoneId = "UTC" };
        var monday = new DateOnly(2026, 6, 1);
        var now = new DateTime(2026, 6, 1, 8, 30, 0, DateTimeKind.Utc);   // 09:00 and 09:00-09:30 are inside min notice
        var booked = (new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc), new DateTime(2026, 6, 1, 10, 30, 0, DateTimeKind.Utc));

        var slots = SlotGenerator.Generate(type, Monday9To11, [booked], monday, 1, now);

        // 09:00 too soon; 09:30 and 10:30 within 15-minute buffer of the 10:00 booking; 10:00 booked.
        slots.ShouldBeEmpty();
    }
}

public class FormValidationTests
{
    private static QuestionSnapshot Q(QuestionType type, bool required = true, params string[] options) =>
        new("k", type, "Q", null, required, options);

    [Theory]
    [InlineData(QuestionType.Email, "a@b.co", true)]
    [InlineData(QuestionType.Email, "nope", false)]
    [InlineData(QuestionType.Number, "12.5", true)]
    [InlineData(QuestionType.Number, "twelve", false)]
    [InlineData(QuestionType.Date, "2026-10-23", true)]
    [InlineData(QuestionType.Date, "23/10/2026", false)]
    [InlineData(QuestionType.YesNo, "Yes", true)]
    [InlineData(QuestionType.YesNo, "maybe", false)]
    [InlineData(QuestionType.ShortText, "line1\nline2", false)]
    public void Validates_by_type(QuestionType type, string value, bool ok) =>
        (FormFlowService.ValidateAnswer(Q(type), value) is null).ShouldBe(ok);

    [Fact]
    public void Choices_must_come_from_the_options()
    {
        FormFlowService.ValidateAnswer(Q(QuestionType.SingleChoice, true, "A", "B"), "C").ShouldNotBeNull();
        FormFlowService.ValidateAnswer(Q(QuestionType.MultiChoice, true, "A", "B"), "A\nB").ShouldBeNull();
    }

    [Fact]
    public void Required_and_optional_blanks()
    {
        FormFlowService.ValidateAnswer(Q(QuestionType.ShortText), "  ").ShouldNotBeNull();
        FormFlowService.ValidateAnswer(Q(QuestionType.ShortText, required: false), null).ShouldBeNull();
    }
}

public class ImageCompressorTests
{
    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.SteelBlue };
            canvas.DrawCircle(width / 2f, height / 2f, Math.Min(width, height) / 3f, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public void Converts_and_resizes()
    {
        var result = ImageCompressor.Compress(Png(800, 600), 70, OutputFormat.Webp, maxDimension: 400);
        result.ContentType.ShouldBe("image/webp");
        result.Width.ShouldBe(400);
        result.Height.ShouldBe(300);
    }

    [Fact]
    public void Rejects_files_that_are_not_images() =>
        Should.Throw<AppException>(() => ImageCompressor.Compress("not an image"u8.ToArray(), 75, OutputFormat.Same, null));

    [Fact]
    public void Jpeg_output_flattens_transparency_onto_white()
    {
        using var bitmap = new SKBitmap(10, 10, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Transparent);
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);

        var result = ImageCompressor.Compress(png.ToArray(), 90, OutputFormat.Jpeg, null);
        using var decoded = SKBitmap.Decode(result.Bytes);
        decoded.GetPixel(5, 5).Red.ShouldBeGreaterThan((byte)240);   // white, not black
    }
}
