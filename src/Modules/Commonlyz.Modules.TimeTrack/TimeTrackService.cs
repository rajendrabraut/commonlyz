using System.Text;
using Commonlyz.Core;
using Commonlyz.Core.Data;
using Commonlyz.Modules.TimeTrack.Data;
using Microsoft.EntityFrameworkCore;

namespace Commonlyz.Modules.TimeTrack;

public sealed record EntryView(
    long Id, string? Description, int? ProjectId, string? ProjectName, string? ProjectColor,
    DateTime StartUtc, DateTime? EndUtc, long Seconds, bool HasOverlap)
{
    public bool IsRunning => EndUtc is null;
}

public sealed record DayView(DateOnly Date, long Seconds, IReadOnlyList<(EntryView Entry, DayPortion Portion)> Items);

public sealed record WeekView(DateOnly Start, string TimeZone, long TotalSeconds, IReadOnlyList<DayView> Days,
    IReadOnlyList<(string Name, string Color, long Seconds)> Projects);

public sealed class TimeTrackService(IDbContextFactory<TimeTrackDb> dbFactory, TimeProvider clock)
{
    /// <summary>Typo guard for manual entries. A forgotten running timer can still exceed it.</summary>
    public static readonly TimeSpan MaxManualDuration = TimeSpan.FromHours(24);

    /// <summary>Stopping a timer this soon after starting it is a misclick: the entry is discarded.</summary>
    public static readonly TimeSpan MinDuration = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    // ── Settings ─────────────────────────────────────────────────────────────

    public async Task<string> GetTimeZoneAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Settings.Where(s => s.UserId == userId).Select(s => s.TimeZoneId).FirstOrDefaultAsync() ?? "UTC";
    }

    public async Task SetTimeZoneAsync(string userId, string timeZoneId)
    {
        AppException.ThrowIf(!Zones.IsValid(timeZoneId), $"Unknown time zone '{timeZoneId}'.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var settings = await db.Settings.FindAsync(userId);
        if (settings is null)
            db.Settings.Add(new UserSettings { UserId = userId, TimeZoneId = timeZoneId });
        else
            settings.TimeZoneId = timeZoneId;
        await db.SaveChangesAsync();
    }

    // ── Timer ────────────────────────────────────────────────────────────────

    public async Task<EntryView?> GetRunningAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var entry = await db.Entries.AsNoTracking().Include(e => e.Project).FirstOrDefaultAsync(e => e.UserId == userId && e.EndUtc == null);
        return entry is null ? null : ToView(entry, clock.UtcNowSeconds(), false);
    }

    /// <summary>
    /// Starts a timer, stopping the user's running one in the same transaction. The running row is read with
    /// UPDLOCK/HOLDLOCK so concurrent starts serialise (last one wins) instead of racing the unique index.
    /// </summary>
    public async Task StartAsync(string userId, string? description, int? projectId)
    {
        description = Text.Optional(description, "Description", TimeEntry.MaxDescriptionLength);
        await using var db = await dbFactory.CreateDbContextAsync();
        await EnsureProjectUsableAsync(db, projectId, currentProjectId: null);

        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync();
            var now = clock.UtcNowSeconds();

            var running = await db.Entries
                .FromSql($"SELECT * FROM [timetrack].[TimeEntry] WITH (UPDLOCK, HOLDLOCK) WHERE [UserId] = {userId} AND [EndUtc] IS NULL")
                .FirstOrDefaultAsync();
            if (running is not null)
                Stop(db, running, now);

            db.Entries.Add(new TimeEntry { UserId = userId, Description = description, ProjectId = projectId, StartUtc = now, UpdatedUtc = now });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        });
    }

    /// <returns><c>false</c> if no timer was running.</returns>
    public async Task<bool> StopAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var running = await db.Entries.FirstOrDefaultAsync(e => e.UserId == userId && e.EndUtc == null);
        if (running is null)
            return false;
        Stop(db, running, clock.UtcNowSeconds());
        await db.SaveChangesAsync();
        return true;
    }

    private static void Stop(TimeTrackDb db, TimeEntry running, DateTime now)
    {
        if (now - running.StartUtc < MinDuration)
        {
            db.Entries.Remove(running);
            return;
        }
        running.EndUtc = now;
        running.UpdatedUtc = now;
    }

    // ── Entries ──────────────────────────────────────────────────────────────

    public async Task AddEntryAsync(string userId, DateTime startUtc, DateTime endUtc, string? description, int? projectId)
    {
        var (start, end) = (Clock.TruncateToSeconds(startUtc), Clock.TruncateToSeconds(endUtc));
        ValidateRange(start, end);
        description = Text.Optional(description, "Description", TimeEntry.MaxDescriptionLength);

        await using var db = await dbFactory.CreateDbContextAsync();
        await EnsureProjectUsableAsync(db, projectId, currentProjectId: null);
        db.Entries.Add(new TimeEntry
        {
            UserId = userId, StartUtc = start, EndUtc = end, Description = description, ProjectId = projectId, UpdatedUtc = clock.UtcNowSeconds(),
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Replaces an entry. On the running entry a null <paramref name="endUtc"/> keeps it running.</summary>
    public async Task UpdateEntryAsync(string userId, long id, DateTime startUtc, DateTime? endUtc, string? description, int? projectId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var entry = AppException.NotFound(await db.Entries.FirstOrDefaultAsync(e => e.Id == id && e.UserId == userId), "Entry");

        var start = Clock.TruncateToSeconds(startUtc);
        var end = endUtc is { } e ? Clock.TruncateToSeconds(e) : (DateTime?)null;
        AppException.ThrowIf(end is null && entry.EndUtc is not null, "Only the running entry can have no end time.");
        ValidateRange(start, end);
        await EnsureProjectUsableAsync(db, projectId, entry.ProjectId);

        entry.StartUtc = start;
        entry.EndUtc = end;
        entry.Description = Text.Optional(description, "Description", TimeEntry.MaxDescriptionLength);
        entry.ProjectId = projectId;
        entry.UpdatedUtc = clock.UtcNowSeconds();
        await db.SaveChangesAsync();
    }

    public async Task DeleteEntryAsync(string userId, long id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Entries.Where(e => e.Id == id && e.UserId == userId).ExecuteDeleteAsync();
    }

    /// <summary>Seven local days from <paramref name="weekStart"/>, entries split at local midnight, overlaps flagged.</summary>
    public async Task<WeekView> GetWeekAsync(string userId, DateOnly weekStart, string timeZoneId)
    {
        var zone = Zones.Get(timeZoneId);
        var (fromUtc, toUtc) = Zones.RangeUtc(weekStart, weekStart.AddDays(6), zone);
        var now = clock.UtcNowSeconds();

        var entries = await LoadRangeAsync(userId, fromUtc, toUtc, now);
        var days = Enumerable.Range(0, 7).Select(i => weekStart.AddDays(i)).ToDictionary(d => d, _ => new List<(EntryView, DayPortion)>());

        foreach (var entry in entries)
        {
            var start = entry.StartUtc < fromUtc ? fromUtc : entry.StartUtc;
            var end = (entry.EndUtc ?? now) > toUtc ? toUtc : entry.EndUtc ?? now;
            foreach (var portion in Zones.Split(start, end, zone))
            {
                if (days.TryGetValue(portion.Date, out var list))
                    list.Add((entry, portion));
            }
        }

        var dayViews = days.OrderBy(d => d.Key)
            .Select(d => new DayView(d.Key, d.Value.Sum(x => x.Item2.Seconds), d.Value.OrderBy(x => x.Item2.StartUtc).ToList()))
            .ToList();

        var projects = dayViews.SelectMany(d => d.Items)
            .GroupBy(x => (x.Entry.ProjectName ?? "(no project)", x.Entry.ProjectColor ?? "#9ca3af"))
            .Select(g => (g.Key.Item1, g.Key.Item2, g.Sum(x => x.Portion.Seconds)))
            .OrderByDescending(x => x.Item3)
            .ToList();

        return new WeekView(weekStart, zone.Id, dayViews.Sum(d => d.Seconds), dayViews, projects);
    }

    /// <summary>CSV of entries starting within the local date range (not split at midnight).</summary>
    public async Task<string> ExportCsvAsync(string userId, DateOnly from, DateOnly to, string timeZoneId)
    {
        var zone = Zones.Get(timeZoneId);
        var (fromUtc, toUtc) = Zones.RangeUtc(from, to, zone);
        var now = clock.UtcNowSeconds();

        await using var db = await dbFactory.CreateDbContextAsync();
        var entries = await db.Entries.AsNoTracking().Include(e => e.Project)
            .Where(e => e.UserId == userId && e.StartUtc >= fromUtc && e.StartUtc < toUtc)
            .OrderBy(e => e.StartUtc).ToListAsync();

        var csv = new StringBuilder(Csv.Row("Date", "Start", "End", "Duration", "Hours", "Project", "Description"));
        foreach (var e in entries)
        {
            var seconds = (long)((e.EndUtc ?? now) - e.StartUtc).TotalSeconds;
            var start = Zones.ToLocal(e.StartUtc, zone);
            csv.Append(Csv.Row(
                Format.Date(DateOnly.FromDateTime(start)),
                start.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                e.EndUtc is { } end ? Zones.ToLocal(end, zone).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "running",
                Format.Duration(seconds),
                Format.Hours(seconds),
                e.Project?.Name,
                e.Description));
        }
        return csv.ToString();
    }

    private async Task<List<EntryView>> LoadRangeAsync(string userId, DateTime fromUtc, DateTime toUtc, DateTime now)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var entries = await db.Entries.AsNoTracking().Include(e => e.Project)
            .Where(e => e.UserId == userId && e.StartUtc < toUtc && (e.EndUtc == null || e.EndUtc > fromUtc))
            .OrderBy(e => e.StartUtc)
            .ToListAsync();

        // Flag (don't block) overlaps: sorted by start, compare each entry with the following ones that start before it ends.
        var overlapping = new HashSet<long>();
        for (var i = 0; i < entries.Count; i++)
        {
            var endI = entries[i].EndUtc ?? now;
            for (var j = i + 1; j < entries.Count && entries[j].StartUtc < endI; j++)
            {
                overlapping.Add(entries[i].Id);
                overlapping.Add(entries[j].Id);
            }
        }

        return [.. entries.Select(e => ToView(e, now, overlapping.Contains(e.Id)))];
    }

    private static EntryView ToView(TimeEntry e, DateTime now, bool overlap) => new(
        e.Id, e.Description, e.ProjectId, e.Project?.Name, e.Project?.ColorHex,
        Clock.AsUtc(e.StartUtc), e.EndUtc is { } end ? Clock.AsUtc(end) : null,
        (long)((e.EndUtc ?? now) - e.StartUtc).TotalSeconds, overlap);

    private void ValidateRange(DateTime start, DateTime? end)
    {
        var now = clock.UtcNowSeconds();
        AppException.ThrowIf(start > now + ClockSkew, "Start can't be in the future.");
        if (end is not { } e)
            return;
        AppException.ThrowIf(e <= start, "End must be after start.");
        AppException.ThrowIf(e > now + ClockSkew, "End can't be in the future.");
        AppException.ThrowIf(e - start > MaxManualDuration, "Entries can be at most 24 hours. Split longer work into several entries.");
    }

    /// <summary>The project must exist; new work can't go to an archived project unless the entry already used it.</summary>
    private static async Task EnsureProjectUsableAsync(TimeTrackDb db, int? projectId, int? currentProjectId)
    {
        if (projectId is not { } id)
            return;
        var project = await db.Projects.Where(p => p.Id == id).Select(p => new { p.ArchivedUtc }).FirstOrDefaultAsync()
            ?? throw new AppException("Project not found.");
        AppException.ThrowIf(project.ArchivedUtc is not null && id != currentProjectId, "Project is archived.");
    }

    // ── Projects ─────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Project>> ListProjectsAsync(bool includeArchived = false)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Projects.AsNoTracking().Where(p => includeArchived || p.ArchivedUtc == null).OrderBy(p => p.Name).ToListAsync();
    }

    public async Task SaveProjectAsync(int? id, string name, string colorHex)
    {
        name = Text.Required(name, "Name", Project.MaxNameLength);
        AppException.ThrowIf(!Text.IsColor(colorHex), "Colour must look like #3b82f6.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var project = id is { } existing
            ? AppException.NotFound(await db.Projects.FindAsync(existing), "Project")
            : db.Projects.Add(new Project { Name = name }).Entity;
        project.Name = name;
        project.ColorHex = colorHex;
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            throw new AppException($"A project named '{name}' already exists.");
        }
    }

    public async Task SetArchivedAsync(int id, bool archived)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var project = AppException.NotFound(await db.Projects.FindAsync(id), "Project");
        project.ArchivedUtc = archived ? clock.UtcNowSeconds() : null;
        await db.SaveChangesAsync();
    }
}
