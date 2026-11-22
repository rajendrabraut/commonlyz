using Kamala.Application.Abstractions;
using Kamala.Application.Conversion;
using Kamala.Application.Jobs;
using Kamala.Domain;
using Kamala.Domain.Conversion;
using Kamala.Domain.Images;

namespace Kamala.Application;

/// <summary>Compress, resize, crop, change ratio, enhance or degrade images.</summary>
public sealed class EditImagesUseCase(JobRunner jobs, IImageEditor editor, ICapabilityProvider capabilities)
{
    public Task<JobResult> ExecuteAsync(IReadOnlyList<IncomingFile> files, ImageEditRequest request, CancellationToken ct)
    {
        request.Validate();
        if (!capabilities.Current.ImageEditing)
            throw new FeatureUnavailableException("Image editing is not available on this server.");

        return jobs.RunAsync(files, (inputs, workspace, token) =>
        {
            RequireImages(inputs);
            return jobs.ForEachAsync(inputs, async (input, t) =>
            {
                var path = workspace.ReserveOutputPath($"{input.Stem}.{request.Format.Resolve(input.Kind).Extension()}");
                var outcome = await editor.EditAsync(input, path, request, t);
                return new OutputFile(path, outcome.Format, outcome.Width, outcome.Height, input);
            }, token);
        }, ct);
    }

    internal static void RequireImages(IReadOnlyList<InputFile> inputs)
    {
        var other = inputs.FirstOrDefault(i => i.Kind.Category() != FileCategory.Image);
        InvalidInputException.ThrowIf(other is not null, $"\"{other?.Stem}\" is not an image.");
    }
}

/// <summary>AI super-resolution: enlarge images 2-4x and restore detail.</summary>
public sealed class UpscaleImagesUseCase(JobRunner jobs, IImageUpscaler upscaler, ICapabilityProvider capabilities)
{
    public Task<JobResult> ExecuteAsync(IReadOnlyList<IncomingFile> files, UpscaleRequest request, CancellationToken ct)
    {
        request.Validate();
        if (!capabilities.Current.AiUpscale)
            throw new FeatureUnavailableException("AI upscaling is not available on this server.");

        return jobs.RunAsync(files, (inputs, workspace, token) =>
        {
            EditImagesUseCase.RequireImages(inputs);
            return jobs.ForEachAsync(inputs, (input, t) => upscaler.UpscaleAsync(input, workspace, request, t), token);
        }, ct);
    }
}

/// <summary>Converts between PDF, Word and images by picking the matching <see cref="IConversionRoute"/>.</summary>
public sealed class ConvertFilesUseCase(JobRunner jobs, IEnumerable<IConversionRoute> routes, ICapabilityProvider capabilities)
{
    private readonly IReadOnlyList<IConversionRoute> _routes = routes.ToList();

    public Task<JobResult> ExecuteAsync(IReadOnlyList<IncomingFile> files, ConversionRequest request, CancellationToken ct)
    {
        request.Validate();
        return jobs.RunAsync(files, (inputs, workspace, token) =>
        {
            var categories = inputs.Select(i => i.Kind.Category()).Distinct().ToList();
            InvalidInputException.ThrowIf(categories.Count > 1, "Convert one kind of file at a time (only images, only PDFs or only Word documents).");
            var source = categories[0];

            var route = _routes.FirstOrDefault(r => r.Source == source && r.Targets.Contains(request.Target))
                ?? throw new InvalidInputException($"Converting {Describe(source)} to {request.Target.ToString().ToUpperInvariant()} is not supported.");
            if (!route.IsAvailable(capabilities.Current))
                throw new FeatureUnavailableException($"Converting {Describe(source)} to {request.Target.ToString().ToUpperInvariant()} is not available on this server.");
            return route.ConvertAsync(inputs, request, workspace, token);
        }, ct);
    }

    /// <summary>Every source/target pair and whether this server can do it right now (drives the UI).</summary>
    public IReadOnlyList<ConversionOption> Options() =>
        _routes.SelectMany(r => r.Targets.Select(t => new ConversionOption(r.Source, t, r.IsAvailable(capabilities.Current)))).ToList();

    private static string Describe(FileCategory category) => category switch
    {
        FileCategory.Image => "images",
        FileCategory.Pdf => "PDF",
        FileCategory.Word => "Word documents",
        _ => "these files",
    };
}

public sealed record ConversionOption(FileCategory Source, ConversionTarget Target, bool Available);

/// <summary>Looks up finished results for download.</summary>
public sealed class DownloadsQuery(IJobStore store)
{
    public StoredFile? File(string fileId) => IsId(fileId) ? store.FindFile(fileId) : null;

    public IReadOnlyList<StoredFile> Job(string jobId) => IsId(jobId) ? store.FindJobFiles(jobId) : [];

    /// <summary>Ids are 32 lowercase hex characters; anything else is rejected before any lookup.</summary>
    private static bool IsId(string id) => id.Length == 32 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
