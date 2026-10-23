using Commonlyz.Core;
using Commonlyz.Core.Data;
using Commonlyz.Modules.ShortLink.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Commonlyz.Modules.ShortLink;

public sealed record RedirectTarget(int Id, string Url, bool IsActive, DateTime? ExpiresUtc);

public sealed record DailyClicks(DateOnly Date, int Count);

public sealed record ClickStats(IReadOnlyList<DailyClicks> Daily, IReadOnlyList<(string Referrer, int Count)> TopReferrers);

public sealed class ShortLinkService(IDbContextFactory<ShortLinkDb> dbFactory, IMemoryCache cache, TimeProvider clock)
{
    public const int GeneratedSlugLength = 7;
    private const int GeneratedSlugAttempts = 5;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public async Task<IReadOnlyList<Link>> ListAsync(string? search = null, int take = 200)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var query = db.Links.AsNoTracking();
        if (Text.NullIfBlank(search) is { } s)
            query = query.Where(l => l.Slug.Contains(s) || l.DestinationUrl.Contains(s));
        return await query.OrderByDescending(l => l.Id).Take(take).ToListAsync();
    }

    public async Task<Link> GetAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return AppException.NotFound(await db.Links.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id), "Link");
    }

    /// <param name="ownHost">This site's host name; links back to it would loop.</param>
    public async Task<Link> CreateAsync(string destinationUrl, string? customSlug, DateTime? expiresUtc, string ownHost)
    {
        var destination = ValidateDestination(destinationUrl, ownHost);
        var now = clock.UtcNowSeconds();
        var expires = expiresUtc is { } e ? Clock.TruncateToSeconds(e) : (DateTime?)null;
        AppException.ThrowIf(expires <= now, "Expiry must be in the future.");

        string? slug = null;
        if (Text.NullIfBlank(customSlug) is { } raw)
        {
            slug = Text.NormalizeSlug(raw);
            if (Text.ValidateSlug(slug, max: ShortLinkDb.MaxSlugLength) is { } error)
                throw new AppException("Slug: " + error);
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        var attempts = slug is null ? GeneratedSlugAttempts : 1;
        for (var i = 0; i < attempts; i++)
        {
            var link = new Link
            {
                Slug = slug ?? Text.RandomSlug(GeneratedSlugLength),
                DestinationUrl = destination,
                ExpiresUtc = expires,
                CreatedUtc = now,
            };
            db.Links.Add(link);
            try
            {
                await db.SaveChangesAsync();
                cache.Remove(CacheKey(link.Slug));   // a "not found" may have been cached
                return link;
            }
            catch (DbUpdateException ex) when (ex.IsUniqueViolation())
            {
                db.ChangeTracker.Clear();
            }
        }

        throw new AppException(slug is null ? "Could not allocate a unique slug, try again." : $"Slug '{slug}' is already taken.");
    }

    public async Task UpdateAsync(int id, string destinationUrl, DateTime? expiresUtc, string ownHost)
    {
        var destination = ValidateDestination(destinationUrl, ownHost);
        var expires = expiresUtc is { } e ? Clock.TruncateToSeconds(e) : (DateTime?)null;
        AppException.ThrowIf(expires <= clock.UtcNowSeconds(), "Expiry must be in the future.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var link = AppException.NotFound(await db.Links.FindAsync(id), "Link");
        link.DestinationUrl = destination;
        link.ExpiresUtc = expires;
        await db.SaveChangesAsync();
        cache.Remove(CacheKey(link.Slug));
    }

    public async Task SetActiveAsync(int id, bool active)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var link = AppException.NotFound(await db.Links.FindAsync(id), "Link");
        link.IsActive = active;
        await db.SaveChangesAsync();
        cache.Remove(CacheKey(link.Slug));
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var slug = await db.Links.Where(l => l.Id == id).Select(l => l.Slug).FirstOrDefaultAsync();
        if (slug is null)
            return;
        await db.Links.Where(l => l.Id == id).ExecuteDeleteAsync();   // clicks cascade
        cache.Remove(CacheKey(slug));
    }

    /// <summary>Daily clicks (UTC, zero-filled) and top referrers over the last <paramref name="days"/> days.</summary>
    public async Task<ClickStats> GetStatsAsync(int linkId, int days = 30)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var from = today.AddDays(-(days - 1));
        var start = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        await using var db = await dbFactory.CreateDbContextAsync();
        var clicks = db.Clicks.AsNoTracking().Where(c => c.LinkId == linkId && c.ClickedUtc >= start);

        var perDay = await clicks.GroupBy(c => c.ClickedUtc.Date)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => DateOnly.FromDateTime(x.Key), x => x.Count);
        var referrers = await clicks.GroupBy(c => c.Referrer ?? "")
            .Select(g => new { g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count).Take(10).ToListAsync();

        return new ClickStats(
            [.. Enumerable.Range(0, days).Select(i => from.AddDays(i)).Select(d => new DailyClicks(d, perDay.GetValueOrDefault(d)))],
            [.. referrers.Select(r => (r.Key.Length == 0 ? "(direct)" : r.Key, r.Count))]);
    }

    /// <summary>Hot path for redirects. Misses are cached too, so slug scanning doesn't hit SQL.</summary>
    public async Task<RedirectTarget?> ResolveAsync(string slug, CancellationToken ct)
    {
        slug = Text.NormalizeSlug(slug);
        if (cache.TryGetValue(CacheKey(slug), out RedirectTarget? cached))
            return cached;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var target = await db.Links.AsNoTracking()
            .Where(l => l.Slug == slug)
            .Select(l => new RedirectTarget(l.Id, l.DestinationUrl, l.IsActive, l.ExpiresUtc))
            .FirstOrDefaultAsync(ct);

        cache.Set(CacheKey(slug), target, CacheTtl);
        return target;
    }

    public static string CacheKey(string slug) => "shortlink:" + slug;

    private static string ValidateDestination(string url, string ownHost)
    {
        var destination = Text.HttpUrl(url, "Destination URL");
        AppException.ThrowIf(
            string.Equals(new Uri(destination).Host, ownHost, StringComparison.OrdinalIgnoreCase),
            "Destination must not point back at this site (redirect loop).");
        return destination;
    }
}
