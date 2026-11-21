using System.Globalization;
using System.Text.Json.Nodes;
using Commonlyz.Files.Application;
using Commonlyz.Files.Application.Abstractions;
using Commonlyz.Files.Domain;
using Commonlyz.Files.Domain.Images;
using Commonlyz.Files.Infrastructure.Processes;
using Microsoft.Extensions.Options;

namespace Commonlyz.Files.Infrastructure.Engines;

/// <summary>Adapter for the Rust imgcore engine: translates the domain edit into imgcore's JSON job.</summary>
public sealed class RustImageEditor(ProcessRunner runner, IOptions<EngineOptions> engines, IOptions<ProcessingLimits> limits) : IImageEditor
{
    public async Task<ImageEditOutcome> EditAsync(InputFile input, string outputPath, ImageEditRequest request, CancellationToken ct)
    {
        var target = request.Format.Resolve(input.Kind);
        var job = new JsonObject
        {
            ["input"] = input.Path,
            ["output"] = outputPath,
            ["format"] = target switch { FileKind.Jpeg => "jpeg", FileKind.Webp => "webp", _ => "png" },
            ["quality"] = request.Quality,
            ["lossy_png"] = request.LossyPng,
            ["target_bytes"] = request.TargetBytes,
            ["max_pixels"] = limits.Value.MaxPixels,
            ["ops"] = new JsonArray(request.Operations.Select(ToJson).ToArray<JsonNode?>()),
        };

        var result = await runner.RunAsync(engines.Value.ImgcorePath!, [], job.ToJsonString(), Path.GetDirectoryName(outputPath)!, engines.Value.Timeout, ct);
        var reply = runner.ParseReply(result, "image");
        return new ImageEditOutcome(reply.GetProperty("width").GetInt32(), reply.GetProperty("height").GetInt32(), target);
    }

    internal static JsonObject ToJson(ImageOperation operation) => operation switch
    {
        CropOperation c => Op("crop", ("x", c.X), ("y", c.Y), ("width", c.Width), ("height", c.Height)),
        CropToRatioOperation r => Op("crop_ratio", ("ratio_w", r.Ratio.Width), ("ratio_h", r.Ratio.Height)),
        PadToRatioOperation p => Op("pad_ratio", ("ratio_w", p.Ratio.Width), ("ratio_h", p.Ratio.Height), ("color", p.Color)),
        ResizeOperation r => Op("resize", ("width", r.Width), ("height", r.Height),
            ("fit", r.Fit.ToString().ToLowerInvariant()), ("upscale", r.AllowUpscale)),
        ScaleOperation s => Op("scale", ("percent", s.Percent)),
        RotateOperation r => Op("rotate", ("degrees", r.Degrees)),
        FlipOperation f => Op("flip", ("axis", f.Axis.ToString().ToLowerInvariant())),
        AdjustOperation a => Op("adjust", ("brightness", a.Brightness), ("contrast", a.Contrast), ("saturation", a.Saturation)),
        SharpenOperation s => Op("sharpen", ("amount", s.Amount), ("radius", s.Radius)),
        DenoiseOperation d => Op("denoise", ("strength", d.Strength)),
        AutoEnhanceOperation => Op("auto_enhance"),
        GrayscaleOperation => Op("grayscale"),
        BlurOperation b => Op("blur", ("sigma", b.Sigma)),
        _ => throw new NotSupportedException($"No imgcore mapping for {operation.GetType().Name}."),
    };

    private static JsonObject Op(string name, params (string Key, object? Value)[] values)
    {
        var obj = new JsonObject { ["op"] = name };
        foreach (var (key, value) in values)
        {
            obj[key] = value switch
            {
                null => null,
                bool b => b,
                int i => i,
                double d => Math.Round(d, 4),
                string s => s,
                _ => Convert.ToString(value, CultureInfo.InvariantCulture),
            };
        }
        return obj;
    }
}
