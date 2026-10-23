using Commonlyz.Core;
using Commonlyz.Core.Data;
using Commonlyz.Core.Modules;
using Commonlyz.Modules.TeamChat.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Commonlyz.Modules.TeamChat;

public sealed class TeamChatModule : IAppModule
{
    public const string Key = "chat";

    public ModuleInfo Info { get; } = new(
        Key, "Chat", "Slack", "Channels, threads and real-time messages for a small team.", "/chat", "message", ModuleGroup.Team);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<TeamChatDb>(Key);
        services.AddSingleton<ChatNotifier>();
        services.AddSingleton<ChatService>();
    }

    public async Task<IReadOnlyList<ModuleStat>> GetStatsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var user = await services.GetRequiredService<ICurrentUser>().GetAsync();
        var since = services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime.AddDays(-1);
        var channels = await services.GetRequiredService<ChatService>().ChannelsAsync(user.Id);
        var ids = channels.Select(c => c.Id).ToList();
        await using var db = await services.GetRequiredService<IDbContextFactory<TeamChatDb>>().CreateDbContextAsync(cancellationToken);
        var today = await db.Messages.CountAsync(m => ids.Contains(m.ChannelId) && m.CreatedUtc >= since && m.DeletedUtc == null, cancellationToken);
        return [new("Channels", Format.Number(channels.Count)), new("Messages (24 h)", Format.Number(today))];
    }
}
