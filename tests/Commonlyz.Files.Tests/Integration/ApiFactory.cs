using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Commonlyz.Files.Tests.Integration;

/// <summary>
/// Hosts the real API with the real engines, configured from environment variables:
/// FILES_IMGCORE, FILES_PYTHON, FILES_DOCENGINE, FILES_MODEL and optionally FILES_SOFFICE.
/// Engine-dependent tests are skipped (pass with a note) when an engine is missing, unless
/// FILES_REQUIRE_ENGINES=1 (set in CI), in which case a missing engine fails the test.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "commonlyz-files-tests-" + Guid.NewGuid().ToString("N"));

    public static bool RequireEngines => Environment.GetEnvironmentVariable("FILES_REQUIRE_ENGINES") == "1";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Files:Storage:WorkDirectory", _work);
        builder.UseSetting("Files:Engines:ImgcorePath", Environment.GetEnvironmentVariable("FILES_IMGCORE") ?? "");
        builder.UseSetting("Files:Engines:PythonPath", Environment.GetEnvironmentVariable("FILES_PYTHON") ?? "");
        builder.UseSetting("Files:Engines:DocEngineDirectory", Environment.GetEnvironmentVariable("FILES_DOCENGINE") ?? "");
        builder.UseSetting("Files:Engines:UpscaleModelPath", Environment.GetEnvironmentVariable("FILES_MODEL") ?? "");
        builder.UseSetting("Files:Engines:SofficePath", Environment.GetEnvironmentVariable("FILES_SOFFICE") ?? "");
        builder.UseSetting("Files:Api:RequestsPerMinute", "1000");
        builder.UseSetting("Files:Limits:MaxFileBytes", (5 * 1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_work))
            Directory.Delete(_work, recursive: true);
    }

    public async Task<JsonElement> CapabilitiesAsync()
    {
        using var client = CreateClient();
        return await client.GetFromJsonAsync<JsonElement>("/v1/capabilities");
    }

    /// <summary>True when the engine is there; otherwise skips the test (or fails it when engines are required).</summary>
    public async Task<bool> HasAsync(string capability)
    {
        var caps = await CapabilitiesAsync();
        var available = capability switch
        {
            "image" => caps.GetProperty("imageEditing").GetBoolean(),
            "ai" => caps.GetProperty("aiUpscale").GetBoolean(),
            "documents" => caps.GetProperty("conversions").EnumerateArray().Any(c => c.GetProperty("from").GetString() == "pdf" && c.GetProperty("available").GetBoolean()),
            "office" => caps.GetProperty("conversions").EnumerateArray().Any(c => c.GetProperty("from").GetString() == "word" && c.GetProperty("available").GetBoolean()),
            _ => throw new ArgumentOutOfRangeException(nameof(capability)),
        };
        if (!available && RequireEngines)
            throw new InvalidOperationException($"The '{capability}' engine is required (FILES_REQUIRE_ENGINES=1) but not available.");
        return available;
    }

    public async Task<HttpResponseMessage> PostAsync(string path, object options, params (string Name, byte[] Bytes)[] files)
    {
        using var client = CreateClient();
        using var form = new MultipartFormDataContent();
        foreach (var (name, bytes) in files)
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(content, "files", name);
        }
        form.Add(new StringContent(JsonSerializer.Serialize(options)), "options");
        return await client.PostAsync(path, form);
    }

    public async Task<byte[]> DownloadAsync(string fileId)
    {
        using var client = CreateClient();
        return await client.GetByteArrayAsync($"/v1/files/{fileId}");
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "files-api";
}
