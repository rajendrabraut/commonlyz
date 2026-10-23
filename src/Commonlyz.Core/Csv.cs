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

    public static readonly Encoding Utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
}
