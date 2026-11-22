namespace Kamala.Api;

/// <summary>HTTP-level settings. Bound from "Files:Api".</summary>
public sealed class ApiOptions
{
    public const string Section = "Files:Api";

    /// <summary>Browser origins allowed to call the API (the MVC site's URL).</summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>Processing requests allowed per client IP per minute.</summary>
    public int RequestsPerMinute { get; set; } = 30;
}
