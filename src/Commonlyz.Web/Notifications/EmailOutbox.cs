using Microsoft.EntityFrameworkCore;

namespace Commonlyz.Web.Notifications;

public sealed record OutboxCounts(int Queued, int Retrying, int Sent, int Failed);

/// <summary>Read and repair operations on the outbox for the admin Email page.</summary>
public sealed class EmailOutbox(IDbContextFactory<NotificationsDb> dbFactory, TimeProvider clock)
{
    public async Task<IReadOnlyList<OutboxEmail>> RecentAsync(int take = 50)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Outbox.AsNoTracking().OrderByDescending(o => o.Id).Take(take).ToListAsync();
    }

    public async Task<OutboxCounts> CountsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var pending = db.Outbox.Where(o => o.SentUtc == null && o.FailedUtc == null);
        return new OutboxCounts(
            await pending.CountAsync(o => o.Attempts == 0),
            await pending.CountAsync(o => o.Attempts > 0),
            await db.Outbox.CountAsync(o => o.SentUtc != null),
            await db.Outbox.CountAsync(o => o.FailedUtc != null));
    }

    /// <summary>Gives a permanently failed email a fresh set of attempts, starting now.</summary>
    public async Task RetryAsync(long id)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Outbox.Where(o => o.Id == id && o.FailedUtc != null).ExecuteUpdateAsync(s => s
            .SetProperty(o => o.FailedUtc, (DateTime?)null)
            .SetProperty(o => o.Attempts, 0)
            .SetProperty(o => o.NextAttemptUtc, now));
    }
}
