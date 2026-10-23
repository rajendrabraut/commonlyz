using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Commonlyz.Core.Modules;

/// <summary>
/// One app inside Commonlyz. Each module lives in its own project, owns its own database schema,
/// and plugs into the host through this contract (services, endpoints, navigation, dashboard stats).
/// </summary>
public interface IAppModule
{
    ModuleInfo Info { get; }

    void AddServices(IServiceCollection services, IConfiguration configuration);

    /// <summary>Non-Blazor endpoints: redirects, file downloads, CSV exports.</summary>
    void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
    }

    /// <summary>Two or three headline numbers for the dashboard card.</summary>
    Task<IReadOnlyList<ModuleStat>> GetStatsAsync(IServiceProvider services, CancellationToken cancellationToken);
}

public enum ModuleGroup
{
    PersonalTools,
    Workflows,
    Team,
}

/// <param name="Key">Stable identifier, also the database schema name.</param>
/// <param name="Route">Admin route, e.g. "/shortlink".</param>
/// <param name="Icon">Name understood by the <c>Icon</c> component.</param>
public sealed record ModuleInfo(
    string Key,
    string Name,
    string Replaces,
    string Description,
    string Route,
    string Icon,
    ModuleGroup Group);

public sealed record ModuleStat(string Label, string Value);

public static class ModuleGroups
{
    public static string Title(ModuleGroup group) => group switch
    {
        ModuleGroup.PersonalTools => "Personal tools",
        ModuleGroup.Workflows => "Workflows",
        ModuleGroup.Team => "Team",
        _ => group.ToString(),
    };
}
