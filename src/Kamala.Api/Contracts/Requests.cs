using Kamala.Domain;
using Kamala.Domain.Conversion;
using Kamala.Domain.Images;

namespace Kamala.Api.Contracts;

// Wire formats (JSON in the multipart "options" field). They are mapped to domain types, which own the validation,
// so the API contract can evolve without leaking into the domain.

public sealed record ImageEditDto(
    string? Format = null, int Quality = 82, bool LossyPng = false, int? TargetKb = null, IReadOnlyList<ImageOperationDto>? Operations = null)
{
    public ImageEditRequest ToDomain() => new(
        (Operations ?? []).Select(o => o.ToDomain()).ToList(),
        ParseImageFormat(Format, ImageFormat.Same),
        Quality,
        LossyPng,
        TargetKb is null ? null : TargetKb * 1024L);

    internal static ImageFormat ParseImageFormat(string? value, ImageFormat fallback) => value?.ToUpperInvariant() switch
    {
        null or "" => fallback,
        "SAME" => ImageFormat.Same,
        "JPEG" or "JPG" => ImageFormat.Jpeg,
        "PNG" => ImageFormat.Png,
        "WEBP" => ImageFormat.Webp,
        _ => throw new InvalidInputException($"Unknown image format '{value}'."),
    };
}

/// <summary>One edit step; <see cref="Type"/> selects which of the other fields apply.</summary>
public sealed record ImageOperationDto(
    string Type,
    int? X = null, int? Y = null, int? Width = null, int? Height = null,
    int? RatioWidth = null, int? RatioHeight = null, string? Color = null,
    string? Fit = null, bool? AllowUpscale = null, double? Percent = null, int? Degrees = null, string? Axis = null,
    double? Brightness = null, double? Contrast = null, double? Saturation = null,
    double? Amount = null, double? Radius = null, int? Strength = null, double? Sigma = null)
{
    public ImageOperation ToDomain() => Type.ToUpperInvariant() switch
    {
        "CROP" => new CropOperation(Req(X), Req(Y), Req(Width), Req(Height)),
        "CROP_RATIO" => new CropToRatioOperation(new AspectRatio(Req(RatioWidth), Req(RatioHeight))),
        "PAD_RATIO" => new PadToRatioOperation(new AspectRatio(Req(RatioWidth), Req(RatioHeight)), Color ?? "#ffffff"),
        "RESIZE" => new ResizeOperation(Width, Height, ParseFit(Fit), AllowUpscale ?? true),
        "SCALE" => new ScaleOperation(Req(Percent)),
        "ROTATE" => new RotateOperation(Req(Degrees)),
        "FLIP" => new FlipOperation(Axis?.ToUpperInvariant() == "VERTICAL" ? FlipAxis.Vertical
            : Axis?.ToUpperInvariant() == "HORIZONTAL" ? FlipAxis.Horizontal
            : throw new InvalidInputException("Flip needs axis horizontal or vertical.")),
        "ADJUST" => new AdjustOperation(Brightness ?? 0, Contrast ?? 0, Saturation ?? 0),
        "SHARPEN" => new SharpenOperation(Amount ?? 1, Radius ?? 1),
        "DENOISE" => new DenoiseOperation(Strength ?? 1),
        "AUTO_ENHANCE" => new AutoEnhanceOperation(),
        "GRAYSCALE" => new GrayscaleOperation(),
        "BLUR" => new BlurOperation(Req(Sigma)),
        _ => throw new InvalidInputException($"Unknown edit step '{Type}'."),
    };

    private T Req<T>(T? value) where T : struct =>
        value ?? throw new InvalidInputException($"The '{Type}' step is missing a value.");

    private static ResizeFit ParseFit(string? fit) => fit?.ToUpperInvariant() switch
    {
        null or "" or "CONTAIN" => ResizeFit.Contain,
        "COVER" => ResizeFit.Cover,
        "FILL" => ResizeFit.Fill,
        _ => throw new InvalidInputException($"Unknown fit '{fit}'."),
    };
}

public sealed record UpscaleDto(int Scale = 2, string? Format = null, int Quality = 92)
{
    public UpscaleRequest ToDomain() => new(Scale, ImageEditDto.ParseImageFormat(Format, ImageFormat.Png), Quality);
}

public sealed record ConversionDto(
    string? To = null, int Dpi = 150, int Quality = 90, string? PageSize = null, int MarginPoints = 0, string? Pages = null, bool MergeIntoOne = true)
{
    public ConversionRequest ToDomain() => new(
        To?.ToUpperInvariant() switch
        {
            "PDF" => ConversionTarget.Pdf,
            "DOCX" or "WORD" => ConversionTarget.Docx,
            "JPEG" or "JPG" => ConversionTarget.Jpeg,
            "PNG" => ConversionTarget.Png,
            "WEBP" => ConversionTarget.Webp,
            _ => throw new InvalidInputException("Choose what to convert to: pdf, docx, jpg, png or webp."),
        },
        Dpi,
        Quality,
        PageSize?.ToUpperInvariant() switch
        {
            null or "" or "FIT" => Domain.Conversion.PageSize.Fit,
            "A4" => Domain.Conversion.PageSize.A4,
            "LETTER" => Domain.Conversion.PageSize.Letter,
            _ => throw new InvalidInputException($"Unknown page size '{PageSize}'."),
        },
        MarginPoints,
        string.IsNullOrWhiteSpace(Pages) ? null : Pages,
        MergeIntoOne);
}
