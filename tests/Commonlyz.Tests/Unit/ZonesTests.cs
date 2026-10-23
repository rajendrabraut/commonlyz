using Commonlyz.Core;

namespace Commonlyz.Tests.Unit;

public class ZonesTests
{
    private static DateTime Utc(int y, int m, int d, int h, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    [Fact]
    public void Split_cuts_an_entry_at_local_midnight()
    {
        var zone = Zones.Get("Europe/Rome");   // UTC+2 in summer
        // 23:00–01:30 local on 14→15 June
        var portions = Zones.Split(Utc(2026, 6, 14, 21), Utc(2026, 6, 14, 23, 30), zone).ToList();

        portions.Count.ShouldBe(2);
        portions[0].Date.ShouldBe(new DateOnly(2026, 6, 14));
        portions[0].Seconds.ShouldBe(3600);
        portions[1].Date.ShouldBe(new DateOnly(2026, 6, 15));
        portions[1].Seconds.ShouldBe(5400);
    }

    [Fact]
    public void Spring_forward_day_is_23_hours()
    {
        var zone = Zones.Get("Europe/London");
        var (start, end) = Zones.RangeUtc(new DateOnly(2026, 3, 29), new DateOnly(2026, 3, 29), zone);
        (end - start).ShouldBe(TimeSpan.FromHours(23));
    }

    [Fact]
    public void Fall_back_day_is_25_hours()
    {
        var zone = Zones.Get("Europe/London");
        var (start, end) = Zones.RangeUtc(new DateOnly(2026, 10, 25), new DateOnly(2026, 10, 25), zone);
        (end - start).ShouldBe(TimeSpan.FromHours(25));
    }

    [Fact]
    public void A_full_DST_day_entry_counts_23_hours_not_24()
    {
        var zone = Zones.Get("Europe/London");
        var (start, end) = Zones.RangeUtc(new DateOnly(2026, 3, 29), new DateOnly(2026, 3, 29), zone);
        Zones.Split(start, end, zone).Single().Seconds.ShouldBe(23 * 3600);
    }

    [Fact]
    public void Skipped_local_time_moves_forward_instead_of_throwing()
    {
        var zone = Zones.Get("Europe/London");
        // 01:30 doesn't exist on 29 March 2026 (clocks jump 01:00 → 02:00); it shifts forward by the gap to 02:30 BST.
        Zones.ToUtc(new DateOnly(2026, 3, 29), new TimeOnly(1, 30), zone).ShouldBe(Utc(2026, 3, 29, 1, 30));
    }

    [Fact]
    public void Start_of_week_is_monday() => Zones.StartOfWeek(new DateOnly(2026, 10, 25)).ShouldBe(new DateOnly(2026, 10, 19));

    [Fact]
    public void Unknown_zone_is_rejected() => Should.Throw<AppException>(() => Zones.Get("Mars/Olympus"));
}
