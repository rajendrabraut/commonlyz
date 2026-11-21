namespace Commonlyz.Files.Domain.Images;

public enum ImageFormat
{
    /// <summary>Keep JPEG/WebP as they are; everything else becomes PNG.</summary>
    Same,
    Jpeg,
    Png,
    Webp,
}

public static class ImageFormats
{
    /// <summary>The concrete output type for an input, resolving <see cref="ImageFormat.Same"/>.</summary>
    public static FileKind Resolve(this ImageFormat format, FileKind input) => format switch
    {
        ImageFormat.Jpeg => FileKind.Jpeg,
        ImageFormat.Png => FileKind.Png,
        ImageFormat.Webp => FileKind.Webp,
        _ => input is FileKind.Jpeg or FileKind.Webp ? input : FileKind.Png,
    };
}

/// <summary>
/// A full image edit: the operations to apply in order, then how to encode the result.
/// Covers compression (quality, palette PNG, target size), resolution, ratio, crop, enhance and deliberate degrading.
/// </summary>
public sealed record ImageEditRequest(
    IReadOnlyList<ImageOperation> Operations,
    ImageFormat Format = ImageFormat.Same,
    int Quality = 82,
    bool LossyPng = false,
    long? TargetBytes = null)
{
    public const int MaxOperations = 20;
    public const long MinTargetBytes = 5 * 1024;

    public void Validate()
    {
        InvalidInputException.ThrowIf(Operations.Count > MaxOperations, $"At most {MaxOperations} edit steps are allowed.");
        InvalidInputException.ThrowIf(Quality is < 1 or > 100, "Quality must be between 1 and 100.");
        InvalidInputException.ThrowIf(!Enum.IsDefined(Format), "Unknown output format.");
        InvalidInputException.ThrowIf(TargetBytes is not null && TargetBytes < MinTargetBytes, "The target size must be at least 5 KB.");
        InvalidInputException.ThrowIf(TargetBytes is not null && Format is not (ImageFormat.Jpeg or ImageFormat.Same),
            "A target file size needs JPEG output.");
        foreach (var operation in Operations)
            operation.Validate();
    }
}

/// <summary>AI super-resolution (Real-ESRGAN): enlarge 2x-4x while restoring detail.</summary>
public sealed record UpscaleRequest(int Scale = 2, ImageFormat Format = ImageFormat.Png, int Quality = 92)
{
    public void Validate()
    {
        InvalidInputException.ThrowIf(Scale is < 2 or > 4, "AI upscale supports 2x, 3x or 4x.");
        InvalidInputException.ThrowIf(Quality is < 1 or > 100, "Quality must be between 1 and 100.");
        InvalidInputException.ThrowIf(Format == ImageFormat.Same, "Choose PNG, JPEG or WebP for the upscaled image.");
    }
}
