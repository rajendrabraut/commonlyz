using Commonlyz.Core;
using Commonlyz.Core.Data;
using Commonlyz.Core.Modules;
using Commonlyz.Modules.SplitShare.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Commonlyz.Modules.SplitShare;

public sealed class SplitShareModule : IAppModule
{
    public const string Key = "splitshare";

    public ModuleInfo Info { get; } = new(
        Key, "SplitShare", "Splitwise", "Shared expenses for housemates and trips: who paid, who owes whom.", "/splitshare", "percent", ModuleGroup.PersonalTools);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<SplitShareDb>(Key);
        services.AddSingleton<SplitShareService>();
    }

    public async Task<IReadOnlyList<ModuleStat>> GetStatsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var db = await services.GetRequiredService<IDbContextFactory<SplitShareDb>>().CreateDbContextAsync(cancellationToken);
        var groups = await db.Groups.CountAsync(cancellationToken);
        var expenses = await db.Expenses.CountAsync(cancellationToken);
        return [new("Groups", Format.Number(groups)), new("Expenses", Format.Number(expenses))];
    }
}
