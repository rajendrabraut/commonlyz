using Commonlyz.Core;
using Commonlyz.Core.Data;
using Commonlyz.Core.Modules;
using Commonlyz.Modules.BudgetPlan.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Commonlyz.Modules.BudgetPlan;

public sealed class BudgetPlanModule : IAppModule
{
    public const string Key = "budget";

    public ModuleInfo Info { get; } = new(
        Key, "Budget", "YNAB", "Envelope budgeting: give every unit of money a job, track accounts and spending.", "/budget", "wallet", ModuleGroup.PersonalTools);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<BudgetPlanDb>(Key);
        services.AddSingleton<BudgetService>();
    }

    public async Task<IReadOnlyList<ModuleStat>> GetStatsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var budget = services.GetRequiredService<BudgetService>();
        var today = DateOnly.FromDateTime(services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime);
        var month = await budget.GetMonthAsync(today);
        var accounts = await budget.AccountsAsync();
        return [new("Ready to assign", Format.Money(month.ReadyToAssign)), new("Net worth", Format.Money(accounts.Sum(a => a.Balance)))];
    }
}
