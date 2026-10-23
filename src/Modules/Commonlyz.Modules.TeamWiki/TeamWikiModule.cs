using Commonlyz.Core;
using Commonlyz.Core.Data;
using Commonlyz.Core.Modules;
using Commonlyz.Modules.TeamWiki.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Commonlyz.Modules.TeamWiki;

public sealed class TeamWikiModule : IAppModule
{
    public const string Key = "wiki";

    public ModuleInfo Info { get; } = new(
        Key, "Wiki", "Notion", "Team wiki: nested Markdown pages, search and full page history.", "/wiki", "file", ModuleGroup.Team);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<TeamWikiDb>(Key);
        services.AddSingleton<WikiService>();
    }

    public async Task<IReadOnlyList<ModuleStat>> GetStatsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var db = await services.GetRequiredService<IDbContextFactory<TeamWikiDb>>().CreateDbContextAsync(cancellationToken);
        var pages = await db.Pages.CountAsync(cancellationToken);
        var edits = await db.Revisions.CountAsync(cancellationToken);
        return [new("Pages", Format.Number(pages)), new("Edits", Format.Number(edits))];
    }
}
