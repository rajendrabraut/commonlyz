using Commonlyz.Core;
using Commonlyz.Core.Data;
using Commonlyz.Core.Modules;
using Commonlyz.Modules.TaskBoard.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Commonlyz.Modules.TaskBoard;

public sealed class TaskBoardModule : IAppModule
{
    public const string Key = "taskboard";

    public ModuleInfo Info { get; } = new(
        Key, "TaskBoard", "Trello", "Kanban boards with drag-and-drop cards, checklists and due dates.", "/taskboard", "kanban", ModuleGroup.Team);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<TaskBoardDb>(Key);
        services.AddSingleton<TaskBoardService>();
    }

    public async Task<IReadOnlyList<ModuleStat>> GetStatsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime);
        await using var db = await services.GetRequiredService<IDbContextFactory<TaskBoardDb>>().CreateDbContextAsync(cancellationToken);
        var open = await db.Cards.CountAsync(c => c.ArchivedUtc == null, cancellationToken);
        var overdue = await db.Cards.CountAsync(c => c.ArchivedUtc == null && c.DueDate < today, cancellationToken);
        return [new("Open cards", Format.Number(open)), new("Overdue", Format.Number(overdue))];
    }
}
