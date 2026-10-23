using Commonlyz.Core;
using SkiaSharp;

namespace Commonlyz.Modules.PixelSqueeze;

public enum OutputFormat
{
    Same,
    Jpeg,
    Webp,
    Png,
}

public sealed record EncodedImage(byte[] Bytes, string Extension, string ContentType, int Width, int Height, string InputFormat);

/// <summary>
/// Decodes, optionally resizes and re-encodes an image with SkiaSharp. Re-encoding strips EXIF/GPS metadata;
/// EXIF orientation is applied to the pixels first so photos don't end up sideways.
/// </summary>
public static class ImageCompressor
{
    public const long MaxInputBytes = 25 * 1024 * 1024;

    /// <summary>Decompression-bomb guard: a tiny PNG can claim gigapixel dimensions.</summary>
    public const long MaxPixels = 40_000_000;

    public static EncodedImage Compress(byte[] input, int quality, OutputFormat format, int? maxDimension)
    {
        AppException.ThrowIf(input.Length == 0, "The file is empty.");
        AppException.ThrowIf(input.Length > MaxInputBytes, $"Files must be at most {Format.Bytes(MaxInputBytes)}.");
        quality = Math.Clamp(quality, 10, 100);

        // SKCodec sniffs the real format from the bytes; file names and Content-Type are not trusted.
        using var data = SKData.CreateCopy(input);
        using var codec = SKCodec.Create(data) ?? throw new AppException("Not a supported image (JPEG, PNG, WebP, GIF or BMP).");
        AppException.ThrowIf((long)codec.Info.Width * codec.Info.Height > MaxPixels, "Image dimensions are too large (max 40 megapixels).");

        using var decoded = SKBitmap.Decode(codec) ?? throw new AppException("The image could not be decoded.");
        using var oriented = ApplyOrientation(decoded, codec.EncodedOrigin);
        using var resized = Resize(oriented, maxDimension);
        var bitmap = resized ?? oriented;

        var inputFormat = codec.EncodedFormat;
        var target = format switch
        {
            OutputFormat.Jpeg => SKEncodedImageFormat.Jpeg,
            OutputFormat.Webp => SKEncodedImageFormat.Webp,
            OutputFormat.Png => SKEncodedImageFormat.Png,
            _ => inputFormat is SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Webp ? inputFormat : SKEncodedImageFormat.Png,
        };

        var bytes = Encode(bitmap, target, quality);
        var (ext, type) = target switch
        {
            SKEncodedImageFormat.Jpeg => ("jpg", "image/jpeg"),
            SKEncodedImageFormat.Webp => ("webp", "image/webp"),
            _ => ("png", "image/png"),
        };
        return new EncodedImage(bytes, ext, type, bitmap.Width, bitmap.Height, inputFormat.ToString().ToLowerInvariant());
    }

    private static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        if (format == SKEncodedImageFormat.Png)
        {
            using var stream = new SKDynamicMemoryWStream();
            using var pixmap = bitmap.PeekPixels();
            pixmap.Encode(stream, new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 9));
            using var png = stream.DetachAsData();
            return png.ToArray();
        }

        // JPEG has no alpha channel: flatten transparent pixels onto white instead of black.
        using var flattened = format == SKEncodedImageFormat.Jpeg && bitmap.AlphaType != SKAlphaType.Opaque ? Flatten(bitmap) : null;
        using var image = SKImage.FromBitmap(flattened ?? bitmap);
        using var encoded = image.Encode(format, quality) ?? throw new AppException("Encoding failed.");
        return encoded.ToArray();
    }

    private static SKBitmap Flatten(SKBitmap source)
    {
        var result = new SKBitmap(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(source, 0, 0, SKSamplingOptions.Default, null);
        return result;
    }

    private static SKBitmap? Resize(SKBitmap source, int? maxDimension)
    {
        if (maxDimension is not { } max || max <= 0 || (source.Width <= max && source.Height <= max))
            return null;
        var scale = (double)max / Math.Max(source.Width, source.Height);
        var size = new SKSizeI(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale)));
        return source.Resize(size, new SKSamplingOptions(SKCubicResampler.Mitchell));
    }

    /// <summary>Bakes the EXIF rotation into the pixels (mirrored variants are treated as their rotation).</summary>
    private static SKBitmap ApplyOrientation(SKBitmap source, SKEncodedOrigin origin)
    {
        var degrees = origin switch
        {
            SKEncodedOrigin.BottomRight or SKEncodedOrigin.BottomLeft => 180,
            SKEncodedOrigin.RightTop or SKEncodedOrigin.LeftTop => 90,
            SKEncodedOrigin.LeftBottom or SKEncodedOrigin.RightBottom => 270,
            _ => 0,
        };
        if (degrees == 0)
            return source.Copy();

        var swap = degrees != 180;
        var rotated = new SKBitmap(swap ? source.Height : source.Width, swap ? source.Width : source.Height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(rotated);
        canvas.Translate(rotated.Width / 2f, rotated.Height / 2f);
        canvas.RotateDegrees(degrees);
        canvas.Translate(-source.Width / 2f, -source.Height / 2f);
        canvas.DrawBitmap(source, 0, 0, SKSamplingOptions.Default, null);
        return rotated;
    }
}
