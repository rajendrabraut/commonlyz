using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Commonlyz.Core;

public static partial class Text
{
    private const string SlugAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    // First path segments the host uses; public slugs/handles must not shadow them.
    private static readonly FrozenSet<string> ReservedSlugs = new[]
    {
        "admin", "api", "account", "login", "logout", "health", "users", "settings", "_framework", "_content", "favicon.ico",
    }.ToFrozenSet(StringComparer.Ordinal);

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex ColorPattern();

    public static string NormalizeSlug(string? slug) => (slug ?? "").Trim().ToLowerInvariant();

    /// <returns>An error message, or <c>null</c> when the (already normalised) slug is valid.</returns>
    public static string? ValidateSlug(string slug, int min = 3, int max = 64)
    {
        if (slug.Length < min || slug.Length > max)
            return $"Must be {min}-{max} characters.";
        if (!SlugPattern().IsMatch(slug))
            return "Use only a-z, 0-9 and inner hyphens.";
        if (ReservedSlugs.Contains(slug))
            return $"'{slug}' is reserved.";
        return null;
    }

    public static string RandomSlug(int length) => RandomNumberGenerator.GetString(SlugAlphabet, length);

    /// <summary>URL-safe random token with 256 bits of entropy (magic links).</summary>
    public static string RandomToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Sha256(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    public static bool IsColor(string? value) => value is not null && ColorPattern().IsMatch(value);

    public static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static string Required(string? value, string field, int maxLength)
    {
        var trimmed = NullIfBlank(value) ?? throw new AppException($"{field} is required.");
        AppException.ThrowIf(trimmed.Length > maxLength, $"{field} must be at most {maxLength} characters.");
        return trimmed;
    }

    public static string? Optional(string? value, string field, int maxLength)
    {
        var trimmed = NullIfBlank(value);
        AppException.ThrowIf(trimmed?.Length > maxLength, $"{field} must be at most {maxLength} characters.");
        return trimmed;
    }

    /// <summary>Absolute http(s) URL without embedded credentials, or an <see cref="AppException"/>.</summary>
    public static string HttpUrl(string? value, string field = "URL", int maxLength = 2048)
    {
        var text = Required(value, field, maxLength);
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrEmpty(uri.Host))
            throw new AppException($"{field} must be an absolute http(s) URL.");
        AppException.ThrowIf(!string.IsNullOrEmpty(uri.UserInfo), $"{field} must not contain credentials.");
        AppException.ThrowIf(uri.AbsoluteUri.Length > maxLength, $"{field} must be at most {maxLength} characters.");
        return uri.AbsoluteUri;
    }
}
