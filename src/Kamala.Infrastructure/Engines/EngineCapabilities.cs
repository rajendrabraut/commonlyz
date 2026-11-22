using System.Security.Cryptography;
using Kamala.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kamala.Infrastructure.Engines;

/// <summary>
/// Checks once which engines are installed. A missing engine only disables the tools that need it, and the
/// upscale model is used only if its SHA-256 matches the pinned hash.
/// </summary>
public sealed class EngineCapabilities(IOptions<EngineOptions> options, ILogger<EngineCapabilities> logger) : ICapabilityProvider
{
    private readonly Lazy<Capabilities> _current = new(() => Probe(options.Value, logger));

    public Capabilities Current => _current.Value;

    public string? SofficePath => ResolveSoffice(options.Value);

    private static Capabilities Probe(EngineOptions o, ILogger logger)
    {
        var image = Exists(o.ImgcorePath);
        if (!image) logger.LogWarning("imgcore not found at '{Path}': image editing disabled", o.ImgcorePath);

        var docs = Exists(o.PythonPath) && Directory.Exists(Path.Combine(o.DocEngineDirectory ?? "", "docengine"));
        if (!docs) logger.LogWarning("docengine not found (python '{Python}', directory '{Dir}'): PDF tools disabled", o.PythonPath, o.DocEngineDirectory);

        var office = ResolveSoffice(o) is not null;
        if (!office) logger.LogWarning("LibreOffice (soffice) not found: Word conversions disabled");

        var upscale = docs && Exists(o.UpscaleModelPath) && ModelMatches(o, logger);
        return new Capabilities(image, docs, office, upscale);
    }

    private static bool ModelMatches(EngineOptions o, ILogger logger)
    {
        using var stream = File.OpenRead(o.UpscaleModelPath!);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        var ok = string.Equals(hash, o.UpscaleModelSha256, StringComparison.OrdinalIgnoreCase);
        if (!ok) logger.LogError("Upscale model hash {Hash} does not match the pinned {Expected}: AI upscale disabled", hash, o.UpscaleModelSha256);
        return ok;
    }

    private static bool Exists(string? path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    private static string? ResolveSoffice(EngineOptions o)
    {
        if (Exists(o.SofficePath))
            return o.SofficePath;
        string[] candidates = OperatingSystem.IsWindows()
            ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.exe"),
               Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LibreOffice", "program", "soffice.exe")]
            : ["/usr/bin/soffice", "/usr/lib/libreoffice/program/soffice", "/opt/libreoffice/program/soffice",
               "/Applications/LibreOffice.app/Contents/MacOS/soffice"];
        return candidates.FirstOrDefault(File.Exists);
    }
}
