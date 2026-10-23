using Commonlyz.Core;
using Commonlyz.Core.Data;
using Commonlyz.Core.Modules;
using Commonlyz.Modules.StockRoom.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Commonlyz.Modules.StockRoom;

public sealed class StockRoomModule : IAppModule
{
    public const string Key = "stockroom";

    public ModuleInfo Info { get; } = new(
        Key, "StockRoom", "Sortly", "Inventory by location with a full movement history behind every count.", "/stockroom", "box", ModuleGroup.Workflows);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<StockRoomDb>(Key);
        services.AddSingleton<StockRoomService>();
    }

    public async Task<IReadOnlyList<ModuleStat>> GetStatsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var stock = await services.GetRequiredService<StockRoomService>().ListStockAsync();
        return [new("Items", Format.Number(stock.Count)), new("Low stock", Format.Number(stock.Count(s => s.IsLow)))];
    }
}
