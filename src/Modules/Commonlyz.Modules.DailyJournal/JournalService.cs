using System.Text;
using Commonlyz.Core;
using Commonlyz.Modules.DailyJournal.Data;
using Microsoft.EntityFrameworkCore;

namespace Commonlyz.Modules.DailyJournal;

public sealed class JournalService(IDbContextFactory<DailyJournalDb> dbFactory, TimeProvider clock)
{
    public const int MaxBodyLength = 100_000;
    public static readonly IReadOnlyList<string> Moods = ["great", "good", "okay", "low", "rough"];

    public async Task<IReadOnlyList<JournalEntry>> SearchAsync(string userId, string? query, string? tag, int take = 100)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var entries = db.Entries.AsNoTracking().Include(e => e.Tags).Where(e => e.UserId == userId);
        if (Text.NullIfBlank(query) is { } q)
            entries = entries.Where(e => (e.Title != null && e.Title.Contains(q)) || e.Body.Contains(q));
        if (Text.NullIfBlank(tag) is { } t)
            entries = entries.Where(e => e.Tags.Any(x => x.Name == t));
        return await entries.OrderByDescending(e => e.EntryDate).ThenByDescending(e => e.Id).Take(take).ToListAsync();
    }

    public async Task<JournalEntry> GetAsync(string userId, int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return AppException.NotFound(
            await db.Entries.AsNoTracking().Include(e => e.Tags).FirstOrDefaultAsync(e => e.Id == id && e.UserId == userId), "Entry");
    }

    public async Task<IReadOnlyList<(string Name, int Count)>> TagsAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var tags = await db.Tags.AsNoTracking().Where(t => t.UserId == userId)
            .Select(t => new { t.Name, Count = t.Entries.Count }).Where(t => t.Count > 0).OrderBy(t => t.Name).ToListAsync();
        return [.. tags.Select(t => (t.Name, t.Count))];
    }

    /// <summary>Days in the month that have at least one entry (for the calendar).</summary>
    public async Task<HashSet<DateOnly>> DaysWithEntriesAsync(string userId, int year, int month)
    {
        var first = new DateOnly(year, month, 1);
        var last = first.AddMonths(1);
        await using var db = await dbFactory.CreateDbContextAsync();
        var days = await db.Entries.Where(e => e.UserId == userId && e.EntryDate >= first && e.EntryDate < last)
            .Select(e => e.EntryDate).Distinct().ToListAsync();
        return [.. days];
    }

    /// <summary>Creates or updates an entry. Tags are comma separated, case-insensitive and created on demand.</summary>
    public async Task<int> SaveAsync(string userId, int? id, DateOnly date, string? title, string? body, string? mood, string? tags)
    {
        title = Text.Optional(title, "Title", JournalEntry.MaxTitleLength);
        body ??= "";
        AppException.ThrowIf(body.Length > MaxBodyLength, "Entry is too long.");
        AppException.ThrowIf(title is null && string.IsNullOrWhiteSpace(body), "Write something first.");
        mood = Text.NullIfBlank(mood);
        AppException.ThrowIf(mood is not null && !Moods.Contains(mood), "Unknown mood.");

        var tagNames = (tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant().TrimStart('#'))
            .Where(t => t.Length > 0)
            .Distinct()
            .ToList();
        AppException.ThrowIf(tagNames.Any(t => t.Length > 40), "Tags must be at most 40 characters.");

        var now = clock.UtcNowSeconds();
        await using var db = await dbFactory.CreateDbContextAsync();
        var entry = id is { } existingId
            ? AppException.NotFound(await db.Entries.Include(e => e.Tags).FirstOrDefaultAsync(e => e.Id == existingId && e.UserId == userId), "Entry")
            : db.Entries.Add(new JournalEntry { UserId = userId, CreatedUtc = now }).Entity;

        entry.EntryDate = date;
        entry.Title = title;
        entry.Body = body;
        entry.Mood = mood;
        entry.UpdatedUtc = now;

        var existing = await db.Tags.Where(t => t.UserId == userId && tagNames.Contains(t.Name)).ToListAsync();
        entry.Tags = [.. tagNames.Select(n => existing.FirstOrDefault(t => t.Name == n) ?? new JournalTag { UserId = userId, Name = n })];

        await db.SaveChangesAsync();
        return entry.Id;
    }

    public async Task DeleteAsync(string userId, int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var entry = await db.Entries.Include(e => e.Tags).FirstOrDefaultAsync(e => e.Id == id && e.UserId == userId);
        if (entry is null)
            return;
        db.Entries.Remove(entry);
        await db.SaveChangesAsync();
    }

    /// <summary>Everything as one Markdown document, readable without this app.</summary>
    public async Task<string> ExportMarkdownAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var entries = await db.Entries.AsNoTracking().Include(e => e.Tags).Where(e => e.UserId == userId)
            .OrderBy(e => e.EntryDate).ThenBy(e => e.Id).ToListAsync();

        var md = new StringBuilder("# Journal export\n\n");
        foreach (var e in entries)
        {
            md.Append("## ").Append(Format.Date(e.EntryDate));
            if (e.Title is not null)
                md.Append(" — ").Append(e.Title);
            md.Append("\n\n");
            if (e.Mood is not null || e.Tags.Count > 0)
                md.Append('*').Append(string.Join(" · ", new[] { e.Mood }.Concat(e.Tags.Select(t => "#" + t.Name)).Where(s => s is not null))).Append("*\n\n");
            md.Append(e.Body.TrimEnd()).Append("\n\n---\n\n");
        }
        return md.ToString();
    }
}
