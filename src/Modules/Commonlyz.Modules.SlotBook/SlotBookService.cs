using System.Net.Mail;
using Commonlyz.Core;
using Commonlyz.Core.Data;
using Commonlyz.Modules.SlotBook.Data;
using Microsoft.EntityFrameworkCore;

namespace Commonlyz.Modules.SlotBook;

public sealed class SlotBookService(IDbContextFactory<SlotBookDb> dbFactory, TimeProvider clock, INotificationSender notifications)
{
    // ── Meeting types ────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<MeetingType>> ListTypesAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.MeetingTypes.AsNoTracking().Include(t => t.Rules).OrderBy(t => t.Name).ToListAsync();
    }

    public async Task<MeetingType> GetTypeAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return AppException.NotFound(await db.MeetingTypes.AsNoTracking().Include(t => t.Rules).FirstOrDefaultAsync(t => t.Id == id), "Meeting type");
    }

    /// <summary>Creates a meeting type with Monday–Friday 09:00–17:00 availability to start from.</summary>
    public async Task<int> CreateTypeAsync(string name, string slug, int durationMinutes, string timeZoneId)
    {
        var type = new MeetingType { Name = Text.Required(name, "Name", 100), Slug = ValidSlug(slug) };
        ApplySettings(type, durationMinutes, 0, 240, 30, timeZoneId);
        type.Rules = [.. Enumerable.Range(1, 5).Select(d => new AvailabilityRule { DayOfWeek = (DayOfWeek)d, StartTime = new(9, 0), EndTime = new(17, 0) })];

        await using var db = await dbFactory.CreateDbContextAsync();
        db.MeetingTypes.Add(type);
        await SaveUniqueAsync(db, type.Slug);
        return type.Id;
    }

    public async Task UpdateTypeAsync(int id, string name, string slug, string? description, string? location,
        int durationMinutes, int bufferMinutes, int minNoticeMinutes, int maxDaysAhead, string timeZoneId, bool isActive)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var type = AppException.NotFound(await db.MeetingTypes.FindAsync(id), "Meeting type");
        type.Name = Text.Required(name, "Name", 100);
        type.Slug = ValidSlug(slug);
        type.Description = Text.Optional(description, "Description", 1000);
        type.Location = Text.Optional(location, "Location", 300);
        type.IsActive = isActive;
        ApplySettings(type, durationMinutes, bufferMinutes, minNoticeMinutes, maxDaysAhead, timeZoneId);
        await SaveUniqueAsync(db, type.Slug);
    }

    public async Task SetRulesAsync(int typeId, IReadOnlyList<(DayOfWeek Day, TimeOnly Start, TimeOnly End)> rules)
    {
        foreach (var (day, start, end) in rules)
            AppException.ThrowIf(end <= start, $"{day}: end must be after start.");
        foreach (var dayRules in rules.GroupBy(r => r.Day))
        {
            var ordered = dayRules.OrderBy(r => r.Start).ToList();
            for (var i = 1; i < ordered.Count; i++)
                AppException.ThrowIf(ordered[i].Start < ordered[i - 1].End, $"{dayRules.Key}: time windows overlap.");
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        AppException.ThrowIf(!await db.MeetingTypes.AnyAsync(t => t.Id == typeId), "Meeting type not found.");
        await db.InTransactionAsync(async () =>
        {
            await db.Rules.Where(r => r.MeetingTypeId == typeId).ExecuteDeleteAsync();
            db.Rules.AddRange(rules.Select(r => new AvailabilityRule { MeetingTypeId = typeId, DayOfWeek = r.Day, StartTime = r.Start, EndTime = r.End }));
            await db.SaveChangesAsync();
        });
    }

    public async Task DeleteTypeAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.MeetingTypes.Where(t => t.Id == id).ExecuteDeleteAsync();
    }

    // ── Slots & booking ──────────────────────────────────────────────────────

    public async Task<MeetingType?> GetPublicTypeAsync(string slug)
    {
        slug = Text.NormalizeSlug(slug);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.MeetingTypes.AsNoTracking().Include(t => t.Rules).FirstOrDefaultAsync(t => t.Slug == slug && t.IsActive);
    }

    /// <summary>Free slots from <paramref name="fromLocalDate"/> (in the meeting type's zone) for <paramref name="days"/> days.</summary>
    public async Task<IReadOnlyList<Slot>> GetSlotsAsync(MeetingType type, DateOnly fromLocalDate, int days)
    {
        var zone = Zones.Get(type.TimeZoneId);
        var (fromUtc, toUtc) = Zones.RangeUtc(fromLocalDate, fromLocalDate.AddDays(days), zone);
        var buffer = TimeSpan.FromMinutes(type.BufferMinutes);

        await using var db = await dbFactory.CreateDbContextAsync();
        // One calendar: bookings of every meeting type block each other.
        var busy = await db.Bookings.AsNoTracking()
            .Where(b => b.Status == BookingStatus.Confirmed && b.StartUtc < toUtc + buffer && b.EndUtc > fromUtc - buffer)
            .Select(b => new { b.StartUtc, b.EndUtc }).ToListAsync();

        return SlotGenerator.Generate(type, type.Rules, busy.Select(b => (Clock.AsUtc(b.StartUtc), Clock.AsUtc(b.EndUtc))),
            fromLocalDate, days, clock.UtcNowSeconds());
    }

    /// <summary>
    /// Books a slot. The overlap check and insert run in one transaction holding UPDLOCK/HOLDLOCK key-range locks,
    /// so two guests racing for the same slot produce exactly one booking; the loser gets a friendly error.
    /// </summary>
    public async Task<Booking> BookAsync(string slug, DateTime startUtc, string guestName, string guestEmail, string? notes)
    {
        var type = await GetPublicTypeAsync(slug) ?? throw new AppException("This booking page isn't available.");
        guestName = Text.Required(guestName, "Name", 100);
        guestEmail = Text.Required(guestEmail, "Email", 256);
        AppException.ThrowIf(!MailAddress.TryCreate(guestEmail, out _), "Enter a valid email address.");
        notes = Text.Optional(notes, "Notes", 1000);

        startUtc = Clock.TruncateToSeconds(startUtc);
        var zone = Zones.Get(type.TimeZoneId);
        var day = Zones.LocalDate(startUtc, zone);
        // Only offered slots can be booked: no arbitrary times via a crafted request.
        var offered = await GetSlotsAsync(type, day, 1);
        var slot = offered.FirstOrDefault(s => s.StartUtc == startUtc);
        AppException.ThrowIf(slot == default, "That time is no longer available. Please pick another slot.");

        var buffer = TimeSpan.FromMinutes(type.BufferMinutes);
        var (checkStart, checkEnd) = (slot.StartUtc - buffer, slot.EndUtc + buffer);

        await using var db = await dbFactory.CreateDbContextAsync();
        var booking = new Booking
        {
            MeetingTypeId = type.Id,
            StartUtc = slot.StartUtc,
            EndUtc = slot.EndUtc,
            GuestName = guestName,
            GuestEmail = guestEmail,
            Notes = notes,
            CancelToken = Text.RandomToken(),
            CreatedUtc = clock.UtcNowSeconds(),
        };

        await db.InTransactionAsync(async () =>
        {
            booking.Id = 0;   // in case a retried attempt got as far as an insert
            var clash = await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*) AS [Value] FROM [slotbook].[Booking] WITH (UPDLOCK, HOLDLOCK)
                WHERE [Status] = 'Confirmed' AND [StartUtc] < {checkEnd} AND [EndUtc] > {checkStart}
                """).SingleAsync();
            if (clash > 0)
                throw new AppException("Someone just booked that time. Please pick another slot.");

            db.Bookings.Add(booking);
            await db.SaveChangesAsync();
        }, System.Data.IsolationLevel.Serializable);

        await notifications.SendAsync(guestEmail, $"Booked: {type.Name}",
            $"You're booked for {type.Name} at {Zones.ToLocal(slot.StartUtc, zone):yyyy-MM-dd HH:mm} ({type.TimeZoneId}).");
        return booking;
    }

    public async Task<(Booking Booking, MeetingType Type)?> GetByTokenAsync(string token)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var booking = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.CancelToken == token);
        if (booking is null)
            return null;
        var type = await db.MeetingTypes.AsNoTracking().FirstAsync(t => t.Id == booking.MeetingTypeId);
        return (booking, type);
    }

    public async Task CancelByTokenAsync(string token)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Bookings.Where(b => b.CancelToken == token)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, BookingStatus.Cancelled));
    }

    public async Task CancelAsync(int bookingId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Bookings.Where(b => b.Id == bookingId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, BookingStatus.Cancelled));
    }

    public async Task<IReadOnlyList<(Booking Booking, string TypeName, string TimeZoneId)>> UpcomingAsync(bool includeCancelled)
    {
        var now = clock.UtcNowSeconds();
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.Bookings.AsNoTracking()
            .Where(b => b.EndUtc >= now && (includeCancelled || b.Status == BookingStatus.Confirmed))
            .Join(db.MeetingTypes, b => b.MeetingTypeId, t => t.Id, (b, t) => new { b, t.Name, t.TimeZoneId })
            .OrderBy(x => x.b.StartUtc).Take(200).ToListAsync();
        return [.. rows.Select(r => (r.b, r.Name, r.TimeZoneId))];
    }

    private static void ApplySettings(MeetingType type, int duration, int buffer, int minNotice, int maxDays, string timeZoneId)
    {
        AppException.ThrowIf(duration is < 5 or > 480, "Duration must be 5–480 minutes.");
        AppException.ThrowIf(buffer is < 0 or > 240, "Buffer must be 0–240 minutes.");
        AppException.ThrowIf(minNotice is < 0 or > 43200, "Minimum notice must be 0–43200 minutes.");
        AppException.ThrowIf(maxDays is < 1 or > 365, "Booking window must be 1–365 days.");
        AppException.ThrowIf(!Zones.IsValid(timeZoneId), $"Unknown time zone '{timeZoneId}'.");
        (type.DurationMinutes, type.BufferMinutes, type.MinNoticeMinutes, type.MaxDaysAhead, type.TimeZoneId) = (duration, buffer, minNotice, maxDays, timeZoneId);
    }

    private static string ValidSlug(string slug)
    {
        var normalized = Text.NormalizeSlug(slug);
        if (Text.ValidateSlug(normalized) is { } error)
            throw new AppException("Link name: " + error);
        return normalized;
    }

    private static async Task SaveUniqueAsync(SlotBookDb db, string slug)
    {
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            throw new AppException($"Link name '{slug}' is already used.");
        }
    }
}
