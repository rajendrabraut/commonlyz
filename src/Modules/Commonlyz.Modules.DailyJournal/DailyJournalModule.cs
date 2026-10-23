using System.Text;
using Commonlyz.Core;
using Commonlyz.Core.Data;
using Commonlyz.Core.Modules;
using Commonlyz.Modules.DailyJournal.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Commonlyz.Modules.DailyJournal;

public sealed class DailyJournalModule : IAppModule
{
    public const string Key = "journal";

    public ModuleInfo Info { get; } = new(
        Key, "Journal", "Day One", "A private journal: dated Markdown entries, tags, moods and search.", "/journal", "book", ModuleGroup.PersonalTools);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<DailyJournalDb>(Key);
        services.AddSingleton<JournalService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/journal/export.md", async (JournalService journal, ICurrentUser user) =>
            Results.File(Encoding.UTF8.GetBytes(await journal.ExportMarkdownAsync((await user.GetAsync()).Id)), "text/markdown", "journal.md"))
            .RequireAuthorization();

    /// <summary>Journals are private, so the dashboard shows counts only.</summary>
    public async Task<IReadOnlyList<ModuleStat>> GetStatsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var user = await services.GetRequiredService<ICurrentUser>().GetAsync();
        await using var db = await services.GetRequiredService<IDbContextFactory<DailyJournalDb>>().CreateDbContextAsync(cancellationToken);
        var mine = await db.Entries.CountAsync(e => e.UserId == user.Id, cancellationToken);
        var last = await db.Entries.Where(e => e.UserId == user.Id).MaxAsync(e => (DateOnly?)e.EntryDate, cancellationToken);
        return [new("My entries", Format.Number(mine)), new("Last entry", last is { } d ? Format.Date(d) : "—")];
    }
}
