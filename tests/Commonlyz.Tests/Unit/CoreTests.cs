using Commonlyz.Core;

namespace Commonlyz.Tests.Unit;

public class CoreTests
{
    [Theory]
    [InlineData("12", 1200)]
    [InlineData("12.5", 1250)]
    [InlineData("12.50", 1250)]
    [InlineData("-3.10", -310)]
    [InlineData("1,234.56", 123456)]
    public void TryParseMoney_parses_to_minor_units(string text, long expected)
    {
        Format.TryParseMoney(text, out var minor).ShouldBeTrue();
        minor.ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.234")]   // sub-cent amounts are rejected, not rounded
    public void TryParseMoney_rejects_invalid(string text) => Format.TryParseMoney(text, out _).ShouldBeFalse();

    [Fact]
    public void Money_formats_sign_and_cents() => Format.Money(-123456, "EUR").ShouldBe("-1,234.56 EUR");

    [Theory]
    [InlineData("=SUM(A1:A9)", "'=SUM(A1:A9)")]   // formula injection neutralised
    [InlineData("@cmd", "'@cmd")]
    [InlineData("-3.5", "-3.5")]                   // negative numbers stay numbers
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    public void Csv_escapes_safely(string input, string expected) => Csv.Escape(input).ShouldBe(expected);

    [Theory]
    [InlineData("docs", null)]
    [InlineData("q3-report", null)]
    [InlineData("ab", "Must be")]
    [InlineData("-docs", "Use only")]
    [InlineData("api", "reserved")]
    [InlineData("login", "reserved")]
    public void ValidateSlug(string slug, string? errorContains)
    {
        var error = Text.ValidateSlug(slug);
        if (errorContains is null)
            error.ShouldBeNull();
        else
            error.ShouldNotBeNull().ShouldContain(errorContains);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,x")]
    [InlineData("ftp://example.com")]
    [InlineData("https://user:pass@example.com")]
    [InlineData("example.com")]
    public void HttpUrl_rejects_unsafe(string url) => Should.Throw<AppException>(() => Text.HttpUrl(url));

    [Fact]
    public void HttpUrl_returns_canonical_form() => Text.HttpUrl("HTTPS://Example.COM").ShouldBe("https://example.com/");

    [Fact]
    public void RandomToken_is_url_safe_and_unique()
    {
        var tokens = Enumerable.Range(0, 100).Select(_ => Text.RandomToken()).ToList();
        tokens.Distinct().Count().ShouldBe(100);
        tokens.ShouldAllBe(t => t.Length == 43 && !t.Contains('+') && !t.Contains('/') && !t.Contains('='));
    }

    [Fact]
    public void Markdown_escapes_raw_html()
    {
        var html = Markdown.Render("**hi** <script>alert(1)</script>").Value;
        html.ShouldContain("<strong>hi</strong>");
        html.ShouldNotContain("<script>");
    }
}
