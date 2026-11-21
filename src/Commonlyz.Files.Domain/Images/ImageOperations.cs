namespace Commonlyz.Files.Domain.Images;

/// <summary>One step of an image edit. Steps run in order; each validates its own parameters.</summary>
public abstract record ImageOperation
{
    public abstract void Validate();
}

/// <summary>Crop to a rectangle, in pixels of the (EXIF-rotated) image.</summary>
public sealed record CropOperation(int X, int Y, int Width, int Height) : ImageOperation
{
    public override void Validate() =>
        InvalidInputException.ThrowIf(X < 0 || Y < 0 || Width < 1 || Height < 1, "The crop area must have a positive size inside the image.");
}

/// <summary>Centre-crop to an aspect ratio such as 16:9.</summary>
public sealed record CropToRatioOperation(AspectRatio Ratio) : ImageOperation
{
    public override void Validate() => Ratio.Validate();
}

/// <summary>Add borders to reach an aspect ratio without cutting anything off.</summary>
public sealed record PadToRatioOperation(AspectRatio Ratio, string Color = "#ffffff") : ImageOperation
{
    public override void Validate()
    {
        Ratio.Validate();
        InvalidInputException.ThrowIf(!IsHexColor(Color), "Border colour must look like #rrggbb.");
    }

    private static bool IsHexColor(string value) =>
        value.Length == 7 && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);
}

public enum ResizeFit
{
    /// <summary>Fit inside the box, keeping the aspect ratio.</summary>
    Contain,

    /// <summary>Fill the box, keeping the aspect ratio, cropping the overflow.</summary>
    Cover,

    /// <summary>Stretch to exactly the box.</summary>
    Fill,
}

/// <summary>Resize to a box; a missing side is derived from the aspect ratio.</summary>
public sealed record ResizeOperation(int? Width, int? Height, ResizeFit Fit = ResizeFit.Contain, bool AllowUpscale = true) : ImageOperation
{
    public const int MaxSide = 20_000;

    public override void Validate()
    {
        InvalidInputException.ThrowIf(Width is null && Height is null, "Give a width, a height, or both.");
        InvalidInputException.ThrowIf(Width is < 1 or > MaxSide || Height is < 1 or > MaxSide, $"Width and height must be between 1 and {MaxSide} pixels.");
    }
}

public sealed record ScaleOperation(double Percent) : ImageOperation
{
    public override void Validate() =>
        InvalidInputException.ThrowIf(Percent is < 1 or > 800 || double.IsNaN(Percent), "Scale must be between 1% and 800%.");
}

public sealed record RotateOperation(int Degrees) : ImageOperation
{
    public override void Validate() =>
        InvalidInputException.ThrowIf(Degrees % 90 != 0, "Rotation must be a multiple of 90 degrees.");
}

public enum FlipAxis
{
    Horizontal,
    Vertical,
}

public sealed record FlipOperation(FlipAxis Axis) : ImageOperation
{
    public override void Validate() =>
        InvalidInputException.ThrowIf(!Enum.IsDefined(Axis), "Flip must be horizontal or vertical.");
}

/// <summary>Brightness, contrast and saturation, each -100..100 (0 = unchanged).</summary>
public sealed record AdjustOperation(double Brightness, double Contrast, double Saturation) : ImageOperation
{
    public override void Validate() =>
        InvalidInputException.ThrowIf(new[] { Brightness, Contrast, Saturation }.Any(v => v is < -100 or > 100 || double.IsNaN(v)),
            "Brightness, contrast and saturation must be between -100 and 100.");
}

/// <summary>Unsharp mask: amount 0..5, radius 0.3..10 pixels.</summary>
public sealed record SharpenOperation(double Amount = 1, double Radius = 1) : ImageOperation
{
    public override void Validate() =>
        InvalidInputException.ThrowIf(Amount is < 0 or > 5 || Radius is < 0.3 or > 10, "Sharpen amount must be 0-5 and radius 0.3-10.");
}

/// <summary>Edge-preserving noise reduction, strength 1 (light) to 3 (strong).</summary>
public sealed record DenoiseOperation(int Strength = 1) : ImageOperation
{
    public override void Validate() =>
        InvalidInputException.ThrowIf(Strength is < 1 or > 3, "Denoise strength must be 1, 2 or 3.");
}

/// <summary>One-click enhance: levels, a little saturation and a gentle sharpen.</summary>
public sealed record AutoEnhanceOperation : ImageOperation
{
    public override void Validate()
    {
    }
}

public sealed record GrayscaleOperation : ImageOperation
{
    public override void Validate()
    {
    }
}

/// <summary>Soften detail on purpose (the opposite of sharpen), sigma 0.3..20.</summary>
public sealed record BlurOperation(double Sigma) : ImageOperation
{
    public override void Validate() =>
        InvalidInputException.ThrowIf(Sigma is < 0.3 or > 20, "Blur must be between 0.3 and 20.");
}

public readonly record struct AspectRatio(int Width, int Height)
{
    public void Validate() =>
        InvalidInputException.ThrowIf(Width is < 1 or > 100 || Height is < 1 or > 100, "Aspect ratio parts must be between 1 and 100.");
}
