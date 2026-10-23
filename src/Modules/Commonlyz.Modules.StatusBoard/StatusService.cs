using Commonlyz.Core;
using Commonlyz.Modules.StatusBoard.Data;
using Microsoft.EntityFrameworkCore;

namespace Commonlyz.Modules.StatusBoard;

public sealed record StatusPage(
    ComponentStatus Overall,
    IReadOnlyList<StatusComponent> Components,
    IReadOnlyList<Incident> Active,
    IReadOnlyList<Incident> Recent);

public sealed class StatusService(IDbContextFactory<StatusBoardDb> dbFactory, TimeProvider clock)
{
    public static readonly TimeSpan HistoryWindow = TimeSpan.FromDays(14);

    public static string Label(ComponentStatus status) => status switch
    {
        ComponentStatus.Operational => "Operational",
        ComponentStatus.Maintenance => "Under maintenance",
        ComponentStatus.Degraded => "Degraded performance",
        ComponentStatus.PartialOutage => "Partial outage",
        _ => "Major outage",
    };

    public static string Headline(ComponentStatus overall) => overall switch
    {
        ComponentStatus.Operational => "All systems operational",
        ComponentStatus.Maintenance => "Scheduled maintenance in progress",
        ComponentStatus.Degraded => "Some systems are degraded",
        ComponentStatus.PartialOutage => "Partial outage",
        _ => "Major outage",
    };

    public async Task<StatusPage> GetPageAsync()
    {
        var since = clock.UtcNowSeconds() - HistoryWindow;
        await using var db = await dbFactory.CreateDbContextAsync();
        var components = await db.Components.AsNoTracking().OrderBy(c => c.Position).ThenBy(c => c.Name).ToListAsync();
        var incidents = await db.Incidents.AsNoTracking().Include(i => i.Updates).Include(i => i.Components)
            .Where(i => i.ResolvedUtc == null || i.ResolvedUtc >= since)
            .OrderByDescending(i => i.StartedUtc).ToListAsync();
        foreach (var i in incidents)
            i.Updates = [.. i.Updates.OrderByDescending(u => u.Id)];

        var overall = components.Count == 0 ? ComponentStatus.Operational : components.Max(c => c.Status);
        return new StatusPage(overall, components, [.. incidents.Where(i => i.ResolvedUtc is null)], [.. incidents.Where(i => i.ResolvedUtc is not null)]);
    }

    public async Task SaveComponentAsync(int? id, string name, string? description)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var component = id is { } existing
            ? AppException.NotFound(await db.Components.FindAsync(existing), "Component")
            : db.Components.Add(new StatusComponent { Name = "", Position = (await db.Components.MaxAsync(c => (int?)c.Position) ?? 0) + 1 }).Entity;
        component.Name = Text.Required(name, "Name", 100);
        component.Description = Text.Optional(description, "Description", 300);
        await db.SaveChangesAsync();
    }

    public async Task SetComponentStatusAsync(int id, ComponentStatus status)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Components.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, status));
    }

    public async Task DeleteComponentAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Components.Where(c => c.Id == id).ExecuteDeleteAsync();
    }

    /// <summary>Opens an incident with its first update and marks the affected components.</summary>
    public async Task<int> OpenIncidentAsync(string title, IncidentImpact impact, IReadOnlyCollection<int> componentIds, ComponentStatus componentStatus, string message)
    {
        AppException.ThrowIf(componentIds.Count == 0, "Choose at least one affected component.");
        var now = clock.UtcNowSeconds();
        await using var db = await dbFactory.CreateDbContextAsync();
        var incident = new Incident
        {
            Title = Text.Required(title, "Title", 200),
            Impact = impact,
            Status = IncidentStatus.Investigating,
            StartedUtc = now,
            Updates = [new IncidentUpdate { Status = IncidentStatus.Investigating, Body = Text.Required(message, "Update", 4000), CreatedUtc = now }],
            Components = [.. componentIds.Distinct().Select(id => new IncidentComponent { ComponentId = id })],
        };
        db.Incidents.Add(incident);
        await db.SaveChangesAsync();
        await db.Components.Where(c => componentIds.Contains(c.Id)).ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, componentStatus));
        return incident.Id;
    }

    /// <summary>
    /// Posts an update. Resolving restores affected components to Operational, unless another
    /// open incident still affects them.
    /// </summary>
    public async Task PostUpdateAsync(int incidentId, IncidentStatus status, string message)
    {
        var now = clock.UtcNowSeconds();
        await using var db = await dbFactory.CreateDbContextAsync();
        var incident = AppException.NotFound(await db.Incidents.Include(i => i.Components).FirstOrDefaultAsync(i => i.Id == incidentId), "Incident");
        AppException.ThrowIf(incident.ResolvedUtc is not null, "This incident is already resolved.");

        incident.Status = status;
        incident.Updates.Add(new IncidentUpdate { Status = status, Body = Text.Required(message, "Update", 4000), CreatedUtc = now });
        if (status == IncidentStatus.Resolved)
            incident.ResolvedUtc = now;
        await db.SaveChangesAsync();

        if (status == IncidentStatus.Resolved)
        {
            var ids = incident.Components.Select(c => c.ComponentId).ToList();
            var stillAffected = await db.IncidentComponents
                .Where(ic => ids.Contains(ic.ComponentId) && ic.IncidentId != incidentId && db.Incidents.Any(i => i.Id == ic.IncidentId && i.ResolvedUtc == null))
                .Select(ic => ic.ComponentId).ToListAsync();
            await db.Components.Where(c => ids.Contains(c.Id) && !stillAffected.Contains(c.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, ComponentStatus.Operational));
        }
    }
}
