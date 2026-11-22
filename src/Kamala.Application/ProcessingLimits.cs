namespace Kamala.Application;

/// <summary>Business limits that keep the service fast and safe. Bound from the "Files:Limits" configuration section.</summary>
public sealed class ProcessingLimits
{
    public const string Section = "Files:Limits";

    public int MaxFiles { get; set; } = 20;
    public long MaxFileBytes { get; set; } = 50L * 1024 * 1024;
    public long MaxTotalBytes { get; set; } = 200L * 1024 * 1024;

    /// <summary>Largest image (input or output) in pixels; guards against decompression bombs.</summary>
    public long MaxPixels { get; set; } = 50_000_000;

    public int MaxPdfPages { get; set; } = 300;

    /// <summary>Largest input for AI upscaling (a 4x result is 16 times bigger).</summary>
    public long MaxUpscalePixels { get; set; } = 4_000_000;

    /// <summary>Files of one job processed in parallel.</summary>
    public int MaxParallelism { get; set; } = Math.Max(2, Environment.ProcessorCount / 2);
}
