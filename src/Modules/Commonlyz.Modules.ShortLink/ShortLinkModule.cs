using Commonlyz.Core;
using Commonlyz.Core.Data;
using Commonlyz.Core.Modules;
using Commonlyz.Modules.ShortLink.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Commonlyz.Modules.ShortLink;

public sealed class ShortLinkModule : IAppModule
{
    public const string Key = "shortlink";

    /// <summary>Public prefix for short links: https://your-host/s/{slug}.</summary>
    public const string PublicPrefix = "/s/";

    public ModuleInfo Info { get; } = new(
        Key, "ShortLink", "Bitly", "Short links that redirect and count clicks.", "/shortlink", "zap", ModuleGroup.PersonalTools);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<ShortLinkDb>(Key);
        services.AddMemoryCache();
        services.AddSingleton<ShortLinkService>();
        services.AddSingleton<ClickQueue>();
        services.AddHostedService<ClickWriter>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(PublicPrefix + "{slug}", RedirectAsync).AllowAnonymous().ExcludeFromDescription();

    public async Task<IReadOnlyList<ModuleStat>> GetStatsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var db = await services.GetRequiredService<IDbContextFactory<ShortLinkDb>>().CreateDbContextAsync(cancellationToken);
        var links = await db.Links.CountAsync(cancellationToken);
        var clicks = await db.Links.SumAsync(l => (long?)l.ClickCount, cancellationToken) ?? 0;
        return [new("Links", Format.Number(links)), new("Clicks", Format.Number(clicks))];
    }

    /// <summary>302 (not 301) so browsers come back every time and clicks are counted; 404 unknown, 410 disabled/expired.</summary>
    private static async Task<IResult> RedirectAsync(
        string slug, HttpContext http, ShortLinkService links, ClickQueue clicks, TimeProvider clock, CancellationToken ct)
    {
        var target = await links.ResolveAsync(slug, ct);
        if (target is null)
            return Results.NotFound();

        var now = clock.UtcNowSeconds();
        if (!target.IsActive || target.ExpiresUtc <= now)
            return Results.StatusCode(StatusCodes.Status410Gone);

        var referrer = http.Request.Headers.Referer.ToString();
        clicks.TryEnqueue(new ClickEvent(target.Id, now,
            referrer.Length == 0 ? null : referrer[..Math.Min(referrer.Length, Click.MaxReferrerLength)]));

        http.Response.Headers.CacheControl = "private, max-age=0";
        return Results.Redirect(target.Url);
    }
}
