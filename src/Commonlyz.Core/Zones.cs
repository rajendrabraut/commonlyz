using NodaTime;

namespace Commonlyz.Core;

/// <summary>The part of a UTC interval that falls on one local calendar day.</summary>
public readonly record struct DayPortion(DateOnly Date, DateTime StartUtc, DateTime EndUtc)
{
    public long Seconds => (long)(EndUtc - StartUtc).TotalSeconds;
}

/// <summary>
/// UTC ⇄ local calendar conversions using NodaTime/TZDB, so days that are 23 or 25 hours long (DST)
/// or that don't start at 00:00 (e.g. America/Santiago) are handled correctly. Store UTC, display local.
/// </summary>
public static class Zones
{
    public static IReadOnlyList<string> AllIds { get; } = [.. DateTimeZoneProviders.Tzdb.Ids.Order(StringComparer.Ordinal)];

    public static DateTimeZone Get(string? id) =>
        string.IsNullOrWhiteSpace(id) ? DateTimeZone.Utc : DateTimeZoneProviders.Tzdb.GetZoneOrNull(id) ?? throw new AppException($"Unknown time zone '{id}'.");

    public static bool IsValid(string? id) => id is not null && DateTimeZoneProviders.Tzdb.GetZoneOrNull(id) is not null;

    /// <summary>UTC instant the local day starts (not always 00:00 local).</summary>
    public static DateTime StartOfDayUtc(DateOnly date, DateTimeZone zone) =>
        zone.AtStartOfDay(new LocalDate(date.Year, date.Month, date.Day)).ToDateTimeUtc();

    /// <summary>Half-open UTC range [start of <paramref name="from"/>, start of the day after <paramref name="to"/>).</summary>
    public static (DateTime StartUtc, DateTime EndUtc) RangeUtc(DateOnly from, DateOnly to, DateTimeZone zone) =>
        (StartOfDayUtc(from, zone), StartOfDayUtc(to.AddDays(1), zone));

    public static DateOnly LocalDate(DateTime utc, DateTimeZone zone)
    {
        var d = Instant.FromDateTimeUtc(Clock.AsUtc(utc)).InZone(zone).Date;
        return new DateOnly(d.Year, d.Month, d.Day);
    }

    public static DateTime ToLocal(DateTime utc, DateTimeZone zone) =>
        Instant.FromDateTimeUtc(Clock.AsUtc(utc)).InZone(zone).LocalDateTime.ToDateTimeUnspecified();

    /// <summary>
    /// Local wall-clock time → UTC. Times skipped by DST shift forward by the gap (01:30 → 02:30); ambiguous times take the earlier instant.
    /// </summary>
    public static DateTime ToUtc(DateOnly date, TimeOnly time, DateTimeZone zone) =>
        zone.AtLeniently(new LocalDateTime(date.Year, date.Month, date.Day, time.Hour, time.Minute, time.Second)).ToDateTimeUtc();

    /// <summary>Splits [start, end) into one portion per local calendar day it touches.</summary>
    public static IEnumerable<DayPortion> Split(DateTime startUtc, DateTime endUtc, DateTimeZone zone)
    {
        var cursor = Clock.AsUtc(startUtc);
        var end = Clock.AsUtc(endUtc);
        while (cursor < end)
        {
            var date = LocalDate(cursor, zone);
            var nextDay = StartOfDayUtc(date.AddDays(1), zone);
            var portionEnd = nextDay < end ? nextDay : end;
            yield return new DayPortion(date, cursor, portionEnd);
            cursor = portionEnd;
        }
    }

    /// <summary>Monday of the ISO week containing <paramref name="date"/>.</summary>
    public static DateOnly StartOfWeek(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
}
