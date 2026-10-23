using System.Reflection;
using Commonlyz.Core.Modules;

namespace Commonlyz.Web;

/// <summary>Every app hosted by Commonlyz. Add a module here (and a project reference) to plug it in.</summary>
public static class ModuleCatalog
{
    public static readonly IReadOnlyList<IAppModule> All =
    [
        new Modules.ShortLink.ShortLinkModule(),
        new Modules.TimeTrack.TimeTrackModule(),
        new Modules.LinkHub.LinkHubModule(),
        new Modules.PixelSqueeze.PixelSqueezeModule(),
        new Modules.DailyJournal.DailyJournalModule(),
        new Modules.SplitShare.SplitShareModule(),
        new Modules.FormFlow.FormFlowModule(),
        new Modules.SlotBook.SlotBookModule(),
        new Modules.StockRoom.StockRoomModule(),
        new Modules.ClientCollect.ClientCollectModule(),
        new Modules.TaskBoard.TaskBoardModule(),
        new Modules.TeamWiki.TeamWikiModule(),
        new Modules.TeamChat.TeamChatModule(),
        new Modules.StatusBoard.StatusBoardModule(),
        new Modules.BudgetPlan.BudgetPlanModule(),
    ];

    public static Assembly[] Assemblies => [.. All.Select(m => m.GetType().Assembly).Distinct()];
}
