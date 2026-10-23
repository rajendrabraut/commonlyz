using System.Globalization;

namespace Commonlyz.Core;

public static class Format
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>Money is stored as integer minor units (cents); never floats.</summary>
    public static string Money(long minor, string currency = "")
    {
        var sign = minor < 0 ? "-" : "";
        var abs = Math.Abs(minor);
        var text = $"{sign}{abs / 100:#,0}.{abs % 100:00}";
        return string.IsNullOrEmpty(currency) ? text : $"{text} {currency}";
    }

    /// <summary>Parses "12", "12.5", "12.50", "-3.10" into minor units. Rejects more than two decimals.</summary>
    public static bool TryParseMoney(string? text, out long minor)
    {
        minor = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (!decimal.TryParse(text.Trim().Replace(",", "", StringComparison.Ordinal), NumberStyles.Number, Culture, out var value))
            return false;
        var scaled = value * 100;
        if (scaled != decimal.Truncate(scaled) || Math.Abs(scaled) > long.MaxValue / 2)
            return false;
        minor = (long)scaled;
        return true;
    }

    public static string MoneyInput(long minor) => (minor / 100m).ToString("0.00", Culture);

    public static string Duration(long seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}";
    }

    public static string Hours(long seconds) => (seconds / 3600m).ToString("0.00", Culture);

    public static string Date(DateTime value) => value.ToString("yyyy-MM-dd", Culture);

    public static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", Culture);

    public static string DateTime(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm", Culture) + " UTC";

    public static string Number(long value) => value.ToString("#,0", Culture);

    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => (bytes / 1024d).ToString("0.0", Culture) + " KB",
        _ => (bytes / 1024d / 1024d).ToString("0.00", Culture) + " MB",
    };

    /// <summary>"3 min ago" style text for timestamps in the past.</summary>
    public static string Ago(DateTime utc, DateTime nowUtc)
    {
        var d = nowUtc - utc;
        return d.TotalSeconds switch
        {
            < 60 => "just now",
            < 3600 => $"{(int)d.TotalMinutes} min ago",
            < 86400 => $"{(int)d.TotalHours} h ago",
            _ => $"{(int)d.TotalDays} d ago",
        };
    }
}
