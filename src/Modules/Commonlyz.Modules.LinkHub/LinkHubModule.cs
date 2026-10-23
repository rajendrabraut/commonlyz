using Commonlyz.Core;
using Commonlyz.Core.Data;
using Commonlyz.Core.Modules;
using Commonlyz.Modules.LinkHub.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Commonlyz.Modules.LinkHub;

public sealed class LinkHubModule : IAppModule
{
    public const string Key = "linkhub";

    public ModuleInfo Info { get; } = new(
        Key, "LinkHub", "Linktree", "Link-in-bio pages on your own domain, with click counts.", "/linkhub", "link", ModuleGroup.PersonalTools);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<LinkHubDb>(Key);
        services.AddSingleton<LinkHubService>();
    }

    /// <summary>Public pages link through here so clicks are counted before redirecting.</summary>
    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/p/{handle}/go/{linkId:int}", async (string handle, int linkId, LinkHubService hub, CancellationToken ct) =>
                await hub.RecordClickAsync(handle, linkId, ct) is { } url ? Results.Redirect(url) : Results.NotFound())
            .AllowAnonymous();

    public async Task<IReadOnlyList<ModuleStat>> GetStatsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var db = await services.GetRequiredService<IDbContextFactory<LinkHubDb>>().CreateDbContextAsync(cancellationToken);
        var profiles = await db.Profiles.CountAsync(cancellationToken);
        var clicks = await db.Links.SumAsync(l => (long?)l.ClickCount, cancellationToken) ?? 0;
        return [new("Profiles", Format.Number(profiles)), new("Clicks", Format.Number(clicks))];
    }
}
