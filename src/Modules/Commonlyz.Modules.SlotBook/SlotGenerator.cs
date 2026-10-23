using Commonlyz.Core;
using Commonlyz.Modules.SlotBook.Data;

namespace Commonlyz.Modules.SlotBook;

public readonly record struct Slot(DateTime StartUtc, DateTime EndUtc);

/// <summary>
/// Turns weekly availability (local times in the meeting type's zone) into concrete UTC slots, using NodaTime
/// so DST weeks are correct. Removes slots that are too soon, too far ahead, or clash with existing bookings
/// (plus buffer). Pure function: easy to test.
/// </summary>
public static class SlotGenerator
{
    public static IReadOnlyList<Slot> Generate(
        MeetingType type, IEnumerable<AvailabilityRule> rules, IEnumerable<(DateTime StartUtc, DateTime EndUtc)> busy,
        DateOnly fromLocalDate, int days, DateTime nowUtc)
    {
        var zone = Zones.Get(type.TimeZoneId);
        var duration = TimeSpan.FromMinutes(type.DurationMinutes);
        var buffer = TimeSpan.FromMinutes(type.BufferMinutes);
        var earliest = nowUtc.AddMinutes(type.MinNoticeMinutes);
        var lastDay = Zones.LocalDate(nowUtc, zone).AddDays(type.MaxDaysAhead);
        var blocked = busy.Select(b => (Start: b.StartUtc - buffer, End: b.EndUtc + buffer)).ToList();
        var byDay = rules.GroupBy(r => r.DayOfWeek).ToDictionary(g => g.Key, g => g.OrderBy(r => r.StartTime).ToList());

        var slots = new List<Slot>();
        for (var i = 0; i < days; i++)
        {
            var date = fromLocalDate.AddDays(i);
            if (date > lastDay || !byDay.TryGetValue(date.DayOfWeek, out var windows))
                continue;

            foreach (var window in windows)
            {
                var windowEnd = Zones.ToUtc(date, window.EndTime, zone);
                for (var start = Zones.ToUtc(date, window.StartTime, zone); start + duration <= windowEnd; start += duration)
                {
                    var end = start + duration;
                    if (start < earliest || blocked.Any(b => b.Start < end && b.End > start))
                        continue;
                    slots.Add(new Slot(start, end));
                }
            }
        }
        return [.. slots.Distinct().OrderBy(s => s.StartUtc)];
    }
}
