using System.Threading.Channels;
using Commonlyz.Modules.ShortLink.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Commonlyz.Modules.ShortLink;

public sealed record ClickEvent(int LinkId, DateTime ClickedUtc, string? Referrer);

/// <summary>
/// Bounded buffer between redirects and <see cref="ClickWriter"/>. When full, clicks are dropped:
/// a lost analytics row is better than a slow redirect.
/// </summary>
public sealed partial class ClickQueue(ILogger<ClickQueue> logger)
{
    private readonly Channel<ClickEvent> _channel = Channel.CreateBounded<ClickEvent>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true },
        dropped => LogDropped(logger, dropped.LinkId));

    public ChannelReader<ClickEvent> Reader => _channel.Reader;

    public void TryEnqueue(ClickEvent click) => _channel.Writer.TryWrite(click);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Click queue full; dropped click for link {LinkId}")]
    private static partial void LogDropped(ILogger logger, int linkId);
}

/// <summary>Writes queued clicks in batches every <see cref="FlushInterval"/>; drains on shutdown.</summary>
public sealed partial class ClickWriter(
    ClickQueue queue,
    IDbContextFactory<ShortLinkDb> dbFactory,
    TimeProvider clock,
    ILogger<ClickWriter> logger) : BackgroundService
{
    public static TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(2);
    private const int BatchSize = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await queue.Reader.WaitToReadAsync(stoppingToken))
            {
                await Task.Delay(FlushInterval, clock, stoppingToken);
                await DrainAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        await DrainAsync(CancellationToken.None);
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        var batch = new List<ClickEvent>(BatchSize);
        while (queue.Reader.TryRead(out var click))
        {
            batch.Add(click);
            if (batch.Count == BatchSize)
            {
                await FlushAsync(batch, ct);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
            await FlushAsync(batch, ct);
    }

    private async Task FlushAsync(List<ClickEvent> batch, CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct);

                // Links deleted since the click was queued would fail the FK and the whole batch.
                var ids = batch.Select(c => c.LinkId).Distinct().ToList();
                var existing = (await db.Links.Where(l => ids.Contains(l.Id)).Select(l => l.Id).ToListAsync(ct)).ToHashSet();
                var clicks = batch.Where(c => existing.Contains(c.LinkId)).ToList();

                db.Clicks.AddRange(clicks.Select(c => new Click { LinkId = c.LinkId, ClickedUtc = c.ClickedUtc, Referrer = c.Referrer }));
                await db.SaveChangesAsync(ct);

                foreach (var group in clicks.GroupBy(c => c.LinkId))
                {
                    var (linkId, count) = (group.Key, group.LongCount());
                    await db.Links.Where(l => l.Id == linkId)
                        .ExecuteUpdateAsync(s => s.SetProperty(l => l.ClickCount, l => l.ClickCount + count), ct);
                }

                await tx.CommitAsync(ct);
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFlushFailed(logger, ex, batch.Count);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to persist {Count} clicks")]
    private static partial void LogFlushFailed(ILogger logger, Exception ex, int count);
}
