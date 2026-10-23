using Commonlyz.Core;
using Commonlyz.Core.Data;
using Commonlyz.Core.Modules;
using Commonlyz.Modules.ClientCollect.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Commonlyz.Modules.ClientCollect;

public sealed class ClientCollectModule : IAppModule
{
    public const string Key = "clientcollect";

    public ModuleInfo Info { get; } = new(
        Key, "ClientCollect", "Content Snare", "Private request pages where clients send answers and files; you accept or ask for changes.",
        "/clientcollect", "inbox", ModuleGroup.Workflows);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<ClientCollectDb>(Key);
        services.AddSingleton<ClientCollectService>();
        services.AddHostedService<ReminderWorker>();
    }

    /// <summary>
    /// Admin-only download. Always an attachment with a generic type: client uploads are never rendered inline
    /// on our origin, so an uploaded HTML/SVG file can't run script in the admin's session.
    /// </summary>
    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/clientcollect/files/{id:int}", async (int id, ClientCollectService service) =>
            await service.GetFileAsync(id) is { } file
                ? Results.File(file.Content, "application/octet-stream", file.FileName)
                : Results.NotFound())
            .RequireAuthorization();

    public async Task<IReadOnlyList<ModuleStat>> GetStatsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var db = await services.GetRequiredService<IDbContextFactory<ClientCollectDb>>().CreateDbContextAsync(cancellationToken);
        var open = await db.Requests.CountAsync(r => r.Status == RequestStatus.Open, cancellationToken);
        var toReview = await db.Items.CountAsync(i => i.Status == ItemStatus.Submitted, cancellationToken);
        return [new("Open requests", Format.Number(open)), new("To review", Format.Number(toReview))];
    }
}

/// <summary>Checks hourly for requests that need a reminder.</summary>
internal sealed partial class ReminderWorker(ClientCollectService service, ILogger<ReminderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                var sent = await service.SendDueRemindersAsync(stoppingToken);
                if (sent > 0)
                    LogSent(logger, sent);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sent {Count} ClientCollect reminders")]
    private static partial void LogSent(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "ClientCollect reminder run failed")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
