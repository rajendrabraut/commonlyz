namespace Kamala.Web;

/// <summary>Bound from "Files:Web".</summary>
public sealed class WebOptions
{
    public const string Section = "Files:Web";

    /// <summary>
    /// Base URL of Kamala.Api as the browser sees it, e.g. "https://files.example.com/api" (IIS
    /// sub-application) or "http://localhost:5280" in development. The page calls "{ApiBaseUrl}/v1/...".
    /// </summary>
    public string ApiBaseUrl { get; set; } = "/api";

    /// <summary>The API's origin for the Content-Security-Policy (empty when the API is same-origin).</summary>
    public string ApiOrigin => Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : "";
}
