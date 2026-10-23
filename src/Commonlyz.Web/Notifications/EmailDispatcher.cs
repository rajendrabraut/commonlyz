using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Commonlyz.Web.Notifications;

/// <summary>
/// Delivers queued emails. Each round atomically claims a batch of due rows (lease via UPDATE … OUTPUT with
/// READPAST, so multiple app instances share the work without double-sending), sends them, then records the
/// outcome. Failures back off exponentially (30 s, 1 min, 2 min … capped at 1 h) up to MaxAttempts.
/// </summary>
public sealed partial class EmailDispatcher(
    IDbContextFactory<NotificationsDb> dbFactory,
    IEmailTransport transport,
    IOptions<EmailOptions> options,
    TimeProvider clock,
    ILogger<EmailDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private readonly EmailOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.DispatcherEnabled)
            return;
        LogStarted(logger, transport.Description);
        using var timer = new PeriodicTimer(_options.PollInterval, clock);
        do
        {
            try
            {
                // Drain: keep going while full batches come back.
                while (await ProcessBatchAsync(stoppingToken) == _options.BatchSize)
                {
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRoundFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <returns>The number of emails claimed in this round.</returns>
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var leaseUntil = now + Lease;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // Explicit READ COMMITTED: READPAST requires it, and a pooled connection may carry another isolation level.
        var batch = await db.Outbox.FromSql($"""
            SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
            UPDATE TOP ({_options.BatchSize}) o
            SET [LeaseUntilUtc] = {leaseUntil}
            OUTPUT inserted.*
            FROM [notifications].[OutboxEmail] o WITH (ROWLOCK, READPAST, UPDLOCK)
            WHERE o.[SentUtc] IS NULL AND o.[FailedUtc] IS NULL AND o.[NextAttemptUtc] <= {now}
              AND (o.[LeaseUntilUtc] IS NULL OR o.[LeaseUntilUtc] < {now})
            """).AsNoTracking().ToListAsync(cancellationToken);
        if (batch.Count == 0)
            return 0;

        var failures = await transport.SendBatchAsync(batch, cancellationToken);

        var sentAt = clock.GetUtcNow().UtcDateTime;
        foreach (var email in batch)
        {
            if (!failures.TryGetValue(email.Id, out var error))
            {
                await db.Outbox.Where(o => o.Id == email.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.SentUtc, sentAt)
                    .SetProperty(o => o.Attempts, o => o.Attempts + 1)
                    .SetProperty(o => o.LeaseUntilUtc, (DateTime?)null)
                    .SetProperty(o => o.LastError, (string?)null), cancellationToken);
                continue;
            }

            var attempts = email.Attempts + 1;
            var giveUp = attempts >= _options.MaxAttempts;
            var message = error.Message.Length > 1000 ? error.Message[..1000] : error.Message;
            await db.Outbox.Where(o => o.Id == email.Id).ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Attempts, attempts)
                .SetProperty(o => o.NextAttemptUtc, sentAt + Backoff(attempts))
                .SetProperty(o => o.FailedUtc, giveUp ? sentAt : (DateTime?)null)
                .SetProperty(o => o.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(o => o.LastError, message), cancellationToken);
            if (giveUp)
                LogGaveUp(logger, error, email.Id, email.ToAddress, attempts);
            else
                LogRetrying(logger, email.Id, attempts, message);
        }
        return batch.Count;
    }

    /// <summary>30 s × 2^(attempt-1), capped at one hour.</summary>
    public static TimeSpan Backoff(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(3600, 30 * Math.Pow(2, Math.Max(0, attempts - 1))));

    [LoggerMessage(Level = LogLevel.Information, Message = "Email dispatcher started: {Transport}")]
    private static partial void LogStarted(ILogger logger, string transport);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Email {Id} failed (attempt {Attempts}), will retry: {Error}")]
    private static partial void LogRetrying(ILogger logger, long id, int attempts, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Email {Id} to {To} failed permanently after {Attempts} attempts")]
    private static partial void LogGaveUp(ILogger logger, Exception ex, long id, string to, int attempts);

    [LoggerMessage(Level = LogLevel.Error, Message = "Email dispatch round failed")]
    private static partial void LogRoundFailed(ILogger logger, Exception ex);
}
