using Commonlyz.Files.Application.Abstractions;
using Commonlyz.Files.Domain;
using Commonlyz.Files.Domain.Conversion;

namespace Commonlyz.Files.Application.Conversion;

/// <summary>
/// One conversion path (e.g. Word to PDF). New conversions are added as new routes, without touching the use case
/// or the other routes (open/closed principle).
/// </summary>
public interface IConversionRoute
{
    FileCategory Source { get; }

    IReadOnlySet<ConversionTarget> Targets { get; }

    bool IsAvailable(Capabilities capabilities);

    Task<IReadOnlyList<OutputFile>> ConvertAsync(IReadOnlyList<InputFile> inputs, ConversionRequest request, IJobWorkspace workspace, CancellationToken ct);
}

internal static class Targets
{
    public static readonly IReadOnlySet<ConversionTarget> Images =
        new HashSet<ConversionTarget> { ConversionTarget.Jpeg, ConversionTarget.Png, ConversionTarget.Webp };

    public static readonly IReadOnlySet<ConversionTarget> Pdf = new HashSet<ConversionTarget> { ConversionTarget.Pdf };

    public static readonly IReadOnlySet<ConversionTarget> Docx = new HashSet<ConversionTarget> { ConversionTarget.Docx };
}
