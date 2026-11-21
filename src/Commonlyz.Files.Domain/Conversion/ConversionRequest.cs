using System.Globalization;

namespace Commonlyz.Files.Domain.Conversion;

/// <summary>What the user wants to end up with.</summary>
public enum ConversionTarget
{
    Pdf,
    Docx,
    Jpeg,
    Png,
    Webp,
}

public enum PageSize
{
    /// <summary>Each page is exactly the size of its image.</summary>
    Fit,
    A4,
    Letter,
}

/// <summary>Options for a conversion between PDF, Word and image formats.</summary>
public sealed record ConversionRequest(
    ConversionTarget Target,
    int Dpi = 150,
    int Quality = 90,
    PageSize PageSize = PageSize.Fit,
    int MarginPoints = 0,
    string? Pages = null,
    bool MergeIntoOne = true)
{
    public void Validate()
    {
        InvalidInputException.ThrowIf(!Enum.IsDefined(Target), "Unknown target format.");
        InvalidInputException.ThrowIf(Dpi is < 36 or > 600, "Resolution must be between 36 and 600 DPI.");
        InvalidInputException.ThrowIf(Quality is < 10 or > 100, "Quality must be between 10 and 100.");
        InvalidInputException.ThrowIf(!Enum.IsDefined(PageSize), "Unknown page size.");
        InvalidInputException.ThrowIf(MarginPoints is < 0 or > 144, "Margin must be between 0 and 144 points (2 inches).");
        if (!string.IsNullOrWhiteSpace(Pages))
            PageRange.Parse(Pages);
    }

    public bool IsImageTarget => Target is ConversionTarget.Jpeg or ConversionTarget.Png or ConversionTarget.Webp;
}

/// <summary>A 1-based page selection such as "1-3,5,8-10".</summary>
public static class PageRange
{
    public const int MaxPage = 10_000;

    public static IReadOnlyList<(int From, int To)> Parse(string spec)
    {
        var ranges = new List<(int, int)>();
        foreach (var part in spec.Replace(" ", "", StringComparison.Ordinal).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var bounds = part.Split('-');
            InvalidInputException.ThrowIf(bounds.Length > 2, $"Invalid page range '{part}'.");
            var ok = int.TryParse(bounds[0], NumberStyles.None, CultureInfo.InvariantCulture, out var from);
            var to = from;
            ok &= bounds.Length == 1 || int.TryParse(bounds[1], NumberStyles.None, CultureInfo.InvariantCulture, out to);
            InvalidInputException.ThrowIf(!ok || from < 1 || to < from || to > MaxPage, $"Invalid page range '{part}'.");
            ranges.Add((from, to));
        }
        InvalidInputException.ThrowIf(ranges.Count == 0, "The page range is empty.");
        return ranges;
    }
}
