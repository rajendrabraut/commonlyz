namespace Commonlyz.Core;

public static class Clock
{
    /// <summary>Current UTC time truncated to whole seconds, matching DATETIME2(0) columns.</summary>
    public static DateTime UtcNowSeconds(this TimeProvider clock) => TruncateToSeconds(clock.GetUtcNow().UtcDateTime);

    public static DateTime TruncateToSeconds(DateTime value)
    {
        var utc = AsUtc(value);
        return new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }

    /// <summary>SQL Server returns <see cref="DateTimeKind.Unspecified"/>; every DateTime in this app is UTC unless named Local.</summary>
    public static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => System.DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
