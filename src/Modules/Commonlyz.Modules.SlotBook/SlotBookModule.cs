using Commonlyz.Core;
using Commonlyz.Core.Data;
using Commonlyz.Core.Modules;
using Commonlyz.Modules.SlotBook.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Commonlyz.Modules.SlotBook;

public sealed class SlotBookModule : IAppModule
{
    public const string Key = "slotbook";

    public ModuleInfo Info { get; } = new(
        Key, "SlotBook", "Calendly", "Share a link; people book a slot in your weekly availability.", "/slotbook", "calendar", ModuleGroup.Workflows);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<SlotBookDb>(Key);
        services.AddSingleton<SlotBookService>();
    }

    public async Task<IReadOnlyList<ModuleStat>> GetStatsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        await using var db = await services.GetRequiredService<IDbContextFactory<SlotBookDb>>().CreateDbContextAsync(cancellationToken);
        var upcoming = await db.Bookings.CountAsync(b => b.Status == BookingStatus.Confirmed && b.StartUtc >= now, cancellationToken);
        var types = await db.MeetingTypes.CountAsync(t => t.IsActive, cancellationToken);
        return [new("Upcoming", Format.Number(upcoming)), new("Meeting types", Format.Number(types))];
    }
}
