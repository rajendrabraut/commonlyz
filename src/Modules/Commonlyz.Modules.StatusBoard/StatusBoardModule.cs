using Commonlyz.Core;
using Commonlyz.Core.Data;
using Commonlyz.Core.Modules;
using Commonlyz.Modules.StatusBoard.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Commonlyz.Modules.StatusBoard;

public sealed class StatusBoardModule : IAppModule
{
    public const string Key = "status";

    public ModuleInfo Info { get; } = new(
        Key, "StatusBoard", "Statuspage", "Public status page with components and incident updates.", "/statusboard", "activity", ModuleGroup.Team);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<StatusBoardDb>(Key);
        services.AddSingleton<StatusService>();
    }

    /// <summary>Machine-readable status for monitors and other sites. Short public cache.</summary>
    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/status.json", async (StatusService status, HttpContext http) =>
        {
            var page = await status.GetPageAsync();
            http.Response.Headers.CacheControl = "public, max-age=30";
            return Results.Json(new
            {
                status = page.Overall.ToString(),
                description = StatusService.Headline(page.Overall),
                components = page.Components.Select(c => new { c.Name, Status = c.Status.ToString() }),
                incidents = page.Active.Select(i => new { i.Title, Status = i.Status.ToString(), Impact = i.Impact.ToString(), i.StartedUtc }),
            });
        }).AllowAnonymous();

    public async Task<IReadOnlyList<ModuleStat>> GetStatsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var page = await services.GetRequiredService<StatusService>().GetPageAsync();
        return [new("Overall", StatusService.Label(page.Overall)), new("Open incidents", Format.Number(page.Active.Count))];
    }
}
