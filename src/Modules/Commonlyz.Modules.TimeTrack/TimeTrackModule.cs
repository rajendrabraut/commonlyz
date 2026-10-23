using Commonlyz.Core;
using Commonlyz.Core.Data;
using Commonlyz.Core.Modules;
using Commonlyz.Modules.TimeTrack.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Commonlyz.Modules.TimeTrack;

public sealed class TimeTrackModule : IAppModule
{
    public const string Key = "timetrack";

    public ModuleInfo Info { get; } = new(
        Key, "TimeTrack", "Toggl Track", "Start/stop timers, weekly totals, manual corrections and CSV export.", "/timetrack", "clock", ModuleGroup.Workflows);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<TimeTrackDb>(Key);
        services.AddSingleton<TimeTrackService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/timetrack/export.csv", async (DateOnly from, DateOnly to, TimeTrackService time, ICurrentUser currentUser) =>
        {
            if (from > to || to.DayNumber - from.DayNumber > 366)
                return Results.BadRequest("Range must be 1-366 days.");
            var user = await currentUser.GetAsync();
            var csv = await time.ExportCsvAsync(user.Id, from, to, await time.GetTimeZoneAsync(user.Id));
            return Results.File(Csv.ToBytes(csv), "text/csv", $"timetrack-{Format.Date(from)}-{Format.Date(to)}.csv");
        }).RequireAuthorization();

    public async Task<IReadOnlyList<ModuleStat>> GetStatsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var clock = services.GetRequiredService<TimeProvider>();
        var since = clock.GetUtcNow().UtcDateTime.AddDays(-7);
        await using var db = await services.GetRequiredService<IDbContextFactory<TimeTrackDb>>().CreateDbContextAsync(cancellationToken);
        var running = await db.Entries.CountAsync(e => e.EndUtc == null, cancellationToken);
        var seconds = await db.Entries.Where(e => e.EndUtc != null && e.StartUtc >= since)
            .SumAsync(e => (long?)EF.Functions.DateDiffSecond(e.StartUtc, e.EndUtc!.Value), cancellationToken) ?? 0;
        return [new("Last 7 days", $"{Format.Hours(seconds)} h"), new("Running", Format.Number(running))];
    }
}
