namespace Kamala.Infrastructure;

/// <summary>Where the engines live and how long they may run. Bound from "Files:Engines".</summary>
public sealed class EngineOptions
{
    public const string Section = "Files:Engines";

    /// <summary>Path to the Rust imgcore executable.</summary>
    public string? ImgcorePath { get; set; }

    /// <summary>Python interpreter with the docengine requirements installed (typically a venv's python).</summary>
    public string? PythonPath { get; set; }

    /// <summary>Directory containing the <c>docengine</c> package.</summary>
    public string? DocEngineDirectory { get; set; }

    /// <summary>Real-ESRGAN general x4v3 ONNX model, verified against <see cref="UpscaleModelSha256"/> at startup.</summary>
    public string? UpscaleModelPath { get; set; }

    public string UpscaleModelSha256 { get; set; } = "924EBAD6532777303582D4CE7811849B88869231A3AE7093E0F21200249DF8D5";

    /// <summary>LibreOffice <c>soffice</c>; looked up in the usual install locations when empty.</summary>
    public string? SofficePath { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan OfficeTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Engine processes running at once across all requests; others wait.</summary>
    public int MaxConcurrentProcesses { get; set; } = Math.Max(2, Environment.ProcessorCount);

    /// <summary>LibreOffice processes running at once (each uses a lot of memory).</summary>
    public int MaxConcurrentOffice { get; set; } = 2;
}

/// <summary>Where job files are kept and for how long. Bound from "Files:Storage".</summary>
public sealed class StorageOptions
{
    public const string Section = "Files:Storage";

    /// <summary>Scratch root; wiped on startup. Must not be inside the web root.</summary>
    public string WorkDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "kamala");

    public TimeSpan Retention { get; set; } = TimeSpan.FromMinutes(30);
}
