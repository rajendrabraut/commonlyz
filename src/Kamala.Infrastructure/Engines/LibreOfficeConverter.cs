using Kamala.Application.Abstractions;
using Kamala.Domain;
using Kamala.Infrastructure.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kamala.Infrastructure.Engines;

/// <summary>
/// Converts Word-family documents with headless LibreOffice. Each run gets a fresh, throwaway profile with macros
/// disabled and link updates off, so a hostile document can't run code or pull remote content.
/// </summary>
public sealed class LibreOfficeConverter(
    ProcessRunner runner, EngineCapabilities capabilities, IOptions<EngineOptions> options, ILogger<LibreOfficeConverter> logger)
    : IOfficeConverter, IDisposable
{
    private const string LockedDownProfile = """
        <?xml version="1.0" encoding="UTF-8"?>
        <oor:items xmlns:oor="http://openoffice.org/2001/registry" xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <item oor:path="/org.openoffice.Office.Common/Security/Scripting"><prop oor:name="MacroSecurityLevel" oor:op="fuse"><value>3</value></prop></item>
          <item oor:path="/org.openoffice.Office.Common/Security/Scripting"><prop oor:name="DisableMacrosExecution" oor:op="fuse"><value>true</value></prop></item>
          <item oor:path="/org.openoffice.Office.Common/Security/Scripting"><prop oor:name="BlockUntrustedRefererLinks" oor:op="fuse"><value>true</value></prop></item>
          <item oor:path="/org.openoffice.Office.Common/Load"><prop oor:name="UpdateDocMode" oor:op="fuse"><value>0</value></prop></item>
          <item oor:path="/org.openoffice.Office.Writer/Content/Update"><prop oor:name="Link" oor:op="fuse"><value>2</value></prop></item>
          <item oor:path="/org.openoffice.Office.Common/Misc"><prop oor:name="UseOpenCL" oor:op="fuse"><value>false</value></prop></item>
        </oor:items>
        """;

    private readonly SemaphoreSlim _gate = new(Math.Max(1, options.Value.MaxConcurrentOffice));

    public async Task<OutputFile> ConvertAsync(InputFile document, OfficeFormat target, IJobWorkspace workspace, CancellationToken ct)
    {
        var soffice = capabilities.SofficePath ?? throw new FeatureUnavailableException("Word conversion is not available on this server.");
        var scratch = workspace.CreateScratchDirectory();
        var profile = Path.Combine(scratch, "profile");
        Directory.CreateDirectory(Path.Combine(profile, "user"));
        await File.WriteAllTextAsync(Path.Combine(profile, "user", "registrymodifications.xcu"), LockedDownProfile, ct);

        // LibreOffice picks the import filter from the extension, so give the input its detected one.
        var input = Path.Combine(scratch, $"document.{document.Kind.Extension()}");
        File.Copy(document.Path, input);
        var outDir = Path.Combine(scratch, "out");
        Directory.CreateDirectory(outDir);
        var extension = target == OfficeFormat.Pdf ? "pdf" : "docx";

        await _gate.WaitAsync(ct);
        try
        {
            var result = await runner.RunAsync(soffice,
            [
                $"-env:UserInstallation={new Uri(profile + Path.DirectorySeparatorChar).AbsoluteUri}",
                "--headless", "--invisible", "--nologo", "--nodefault", "--nofirststartwizard", "--nolockcheck", "--norestore",
                "--convert-to", extension, "--outdir", outDir, input,
            ], null, scratch, options.Value.OfficeTimeout, ct);

            var produced = Directory.GetFiles(outDir, $"*.{extension}").FirstOrDefault();
            if (produced is null)
            {
                logger.LogError("LibreOffice produced no output (exit {ExitCode}): {StdOut} {StdErr}", result.ExitCode, result.StdOut, result.StdErr);
                throw new InvalidInputException("The document could not be converted. It may be damaged or password-protected.");
            }

            var destination = workspace.ReserveOutputPath($"{document.Stem}.{extension}");
            File.Move(produced, destination);
            return new OutputFile(destination, target == OfficeFormat.Pdf ? FileKind.Pdf : FileKind.Docx, Source: document);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
