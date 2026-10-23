using System.Text;

namespace Commonlyz.Core;

/// <summary>Minimal RFC 4180 writer with spreadsheet formula-injection protection.</summary>
public static class Csv
{
    public static string Row(params object?[] values) =>
        string.Join(',', values.Select(v => Escape(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)))) + "\r\n";

    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        // =, +, -, @ at the start make Excel evaluate the cell as a formula.
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' && !double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out _))
            value = "'" + value;

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>UTF-8 with BOM so Excel detects the encoding.</summary>
    public static byte[] ToBytes(string csv) => [.. Utf8WithBom.GetPreamble(), .. Utf8WithBom.GetBytes(csv)];
}
