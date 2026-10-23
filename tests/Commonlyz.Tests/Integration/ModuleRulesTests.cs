using Commonlyz.Core;
using Commonlyz.Modules.BudgetPlan;
using Commonlyz.Modules.BudgetPlan.Data;
using Commonlyz.Modules.ClientCollect;
using Commonlyz.Modules.ClientCollect.Data;
using Commonlyz.Modules.LinkHub;
using Commonlyz.Modules.ShortLink;
using Commonlyz.Modules.SlotBook;
using Commonlyz.Modules.StockRoom;
using Commonlyz.Modules.TeamChat;
using Commonlyz.Modules.TeamWiki;
using Commonlyz.Modules.TimeTrack;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Commonlyz.Tests.Integration;

/// <summary>The rule each app must get right, exercised against real SQL Server — including under concurrency.</summary>
[Collection(AppTestGroup.Name)]
public class ModuleRulesTests(AppFactory app)
{
    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    private static async Task<(int Ok, int Rejected)> RaceAsync(int count, Func<int, Task> action)
    {
        var results = await Task.WhenAll(Enumerable.Range(0, count).Select(async i =>
        {
            try
            {
                await action(i);
                return true;
            }
            catch (AppException)
            {
                return false;
            }
        }));
        return (results.Count(r => r), results.Count(r => !r));
    }

    [Fact]
    public async Task ShortLink_concurrent_creates_of_one_slug_produce_exactly_one_link()
    {
        var links = app.Get<ShortLinkService>();
        var slug = Unique("race");
        var (ok, rejected) = await RaceAsync(10, i => links.CreateAsync($"https://example.com/{i}", slug, null, "commonlyz.test"));
        ok.ShouldBe(1);
        rejected.ShouldBe(9);
    }

    [Fact]
    public async Task ShortLink_rejects_redirect_loops_and_resolves_case_insensitively()
    {
        var links = app.Get<ShortLinkService>();
        await Should.ThrowAsync<AppException>(() => links.CreateAsync("https://commonlyz.test/s/x", null, null, "commonlyz.test"));
        var slug = Unique("case");
        await links.CreateAsync("https://example.com", slug, null, "commonlyz.test");
        (await links.ResolveAsync(slug.ToUpperInvariant(), default)).ShouldNotBeNull();
    }

    [Fact]
    public async Task TimeTrack_concurrent_starts_leave_exactly_one_running_timer()
    {
        var time = app.Get<TimeTrackService>();
        var user = Unique("user");
        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => time.StartAsync(user, $"task {i}", null)));

        await using var db = await app.Get<IDbContextFactory<Commonlyz.Modules.TimeTrack.Data.TimeTrackDb>>().CreateDbContextAsync();
        (await db.Entries.CountAsync(e => e.UserId == user && e.EndUtc == null)).ShouldBe(1);
    }

    [Fact]
    public async Task TimeTrack_rejects_bad_manual_entries()
    {
        var time = app.Get<TimeTrackService>();
        var start = DateTime.UtcNow.AddHours(-30);
        await Should.ThrowAsync<AppException>(() => time.AddEntryAsync("u", start, start.AddHours(25), null, null));   // > 24 h
        await Should.ThrowAsync<AppException>(() => time.AddEntryAsync("u", start, start, null, null));               // zero length
        await Should.ThrowAsync<AppException>(() => time.AddEntryAsync("u", DateTime.UtcNow.AddHours(1), DateTime.UtcNow.AddHours(2), null, null));   // future
    }

    [Fact]
    public async Task SlotBook_concurrent_bookings_of_one_slot_produce_exactly_one_booking()
    {
        var slots = app.Get<SlotBookService>();
        var slug = Unique("book");
        var typeId = await slots.CreateTypeAsync("Call", slug, 30, "UTC");
        await slots.UpdateTypeAsync(typeId, "Call", slug, null, null, 30, 0, 0, 30, "UTC", true);
        await slots.SetRulesAsync(typeId, [.. Enum.GetValues<DayOfWeek>().Select(d => (d, new TimeOnly(0, 0), new TimeOnly(23, 30)))]);

        var type = (await slots.GetPublicTypeAsync(slug))!;
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);
        var slot = (await slots.GetSlotsAsync(type, tomorrow, 1)).First();

        var (ok, rejected) = await RaceAsync(10, i => slots.BookAsync(slug, slot.StartUtc, $"Guest {i}", $"g{i}@example.com", null));
        ok.ShouldBe(1);
        rejected.ShouldBe(9);
        (await slots.GetSlotsAsync(type, tomorrow, 1)).ShouldNotContain(slot);
    }

    [Fact]
    public async Task StockRoom_concurrent_withdrawals_never_go_negative()
    {
        var stock = app.Get<StockRoomService>();
        await stock.SaveLocationAsync(null, Unique("loc"), null);
        var location = (await stock.ListLocationsAsync()).Last();
        var itemId = await stock.SaveItemAsync(null, Unique("SKU"), "Widget", "pcs", null, null);
        await stock.ReceiveAsync(itemId, location.Id, 50, null, "test");

        var (ok, rejected) = await RaceAsync(10, _ => stock.ConsumeAsync(itemId, location.Id, 10, null, "test"));

        ok.ShouldBe(5);
        rejected.ShouldBe(5);
        (await stock.GetItemAsync(itemId)).Stock.Total.ShouldBe(0);
    }

    [Fact]
    public async Task Serializable_transactions_dont_leak_their_isolation_level_into_the_connection_pool()
    {
        var stock = app.Get<StockRoomService>();
        await stock.SaveLocationAsync(null, Unique("iso"), null);
        var location = (await stock.ListLocationsAsync()).Last();
        var itemId = await stock.SaveItemAsync(null, Unique("ISO"), "Nut", "pcs", null, null);
        await stock.ReceiveAsync(itemId, location.Id, 5, null, "t");   // runs SERIALIZABLE

        // The pool hands back the most recently used connection; it must be at the default level again.
        await using var db = await app.Get<IDbContextFactory<Commonlyz.Modules.StockRoom.Data.StockRoomDb>>().CreateDbContextAsync();
        var level = await db.Database.SqlQuery<short>($"SELECT transaction_isolation_level AS [Value] FROM sys.dm_exec_sessions WHERE session_id = @@SPID").SingleAsync();
        level.ShouldBe((short)2, "2 = READ COMMITTED, 4 = SERIALIZABLE");
    }

    [Fact]
    public async Task StockRoom_stocktake_records_the_difference_and_transfer_moves_both_sides()
    {
        var stock = app.Get<StockRoomService>();
        await stock.SaveLocationAsync(null, Unique("a"), null);
        await stock.SaveLocationAsync(null, Unique("b"), null);
        var locations = (await stock.ListLocationsAsync()).TakeLast(2).ToList();
        var itemId = await stock.SaveItemAsync(null, Unique("SKU"), "Bolt", "pcs", 5, null);

        await stock.ReceiveAsync(itemId, locations[0].Id, 20, null, "t");
        await stock.AdjustToAsync(itemId, locations[0].Id, 17, "count", "t");
        await stock.TransferAsync(itemId, locations[0].Id, locations[1].Id, 7, null, "t");

        var (item, history) = await stock.GetItemAsync(itemId);
        item.ByLocation[locations[0].Id].ShouldBe(10);
        item.ByLocation[locations[1].Id].ShouldBe(7);
        history.Count.ShouldBe(4);   // receive, adjust(-3), transfer out, transfer in
    }

    [Fact]
    public async Task Budget_invariant_holds_with_income_spending_and_transfers()
    {
        var budget = app.Get<BudgetService>();
        await budget.SeedStarterCategoriesAsync();
        var category = (await budget.GroupsAsync()).First().Categories.First();
        var month = BudgetService.MonthOf(DateOnly.FromDateTime(DateTime.UtcNow));

        var checking = await budget.CreateAccountAsync(Unique("chk"), AccountType.Checking, 300_000, month);
        var savings = await budget.CreateAccountAsync(Unique("sav"), AccountType.Savings, 0, month);
        await budget.AssignAsync(category.Id, month, 50_000);
        await budget.AddTransactionAsync(checking, month, "Shop", category.Id, -12_345, null);
        await budget.TransferAsync(checking, savings, month, 100_000, null);

        var view = await budget.GetMonthAsync(month);
        var accounts = await budget.AccountsAsync();
        (view.TotalAvailable + view.ReadyToAssign).ShouldBe(accounts.Sum(a => a.Balance));

        await Should.ThrowAsync<AppException>(() => budget.AddTransactionAsync(checking, month, "x", null, -100, null));   // outflow needs a category
    }

    [Fact]
    public async Task Wiki_blocks_cycles_and_stale_saves()
    {
        var wiki = app.Get<WikiService>();
        var parent = await wiki.CreateAsync(null, "Parent", "t");
        var child = await wiki.CreateAsync(parent, "Child", "t");

        var p = await wiki.GetAsync(parent);
        await Should.ThrowAsync<AppException>(() => wiki.SaveAsync(parent, p.Title, p.Body, child, p.RowVersion, "t"));

        var stale = (await wiki.GetAsync(child)).RowVersion;
        await wiki.SaveAsync(child, "Child v2", "first", parent, stale, "alice");
        await Should.ThrowAsync<AppException>(() => wiki.SaveAsync(child, "Child v3", "second", parent, stale, "bob"));
        (await wiki.RevisionsAsync(child)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Chat_private_channels_dont_leak_through_messages_or_search()
    {
        var chat = app.Get<ChatService>();
        var alice = new AppUserInfo(Unique("alice"), "Alice", "a@x", false);
        var mallory = new AppUserInfo(Unique("mallory"), "Mallory", "m@x", false);
        var secret = Unique("secret");

        var channelId = await chat.CreateChannelAsync(alice.Id, Unique("priv"), null, isPrivate: true, []);
        await chat.PostAsync(alice, channelId, $"the code is {secret}");

        (await chat.ChannelsAsync(mallory.Id)).ShouldNotContain(c => c.Id == channelId);
        (await chat.SearchAsync(mallory.Id, secret)).ShouldBeEmpty();
        await Should.ThrowAsync<AppException>(() => chat.MessagesAsync(mallory.Id, channelId));
        await Should.ThrowAsync<AppException>(() => chat.PostAsync(mallory, channelId, "hi"));
        (await chat.SearchAsync(alice.Id, secret)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task LinkHub_hides_unpublished_profiles_and_draft_links()
    {
        var hub = app.Get<LinkHubService>();
        var handle = Unique("h").ToLowerInvariant();
        var id = await hub.CreateProfileAsync(handle, "Jane");
        await hub.AddLinkAsync(id, "Live", "https://example.com/live", isDraft: false);
        await hub.AddLinkAsync(id, "Draft", "https://example.com/draft", isDraft: true);

        (await hub.GetPublicAsync(handle)).ShouldBeNull();
        await hub.UpdateProfileAsync(id, handle, "Jane", null, null, "#2563eb", isPublished: true);
        (await hub.GetPublicAsync(handle))!.Links.Select(l => l.Title).ShouldBe(["Live"]);
    }

    [Fact]
    public async Task ClientCollect_reminders_stop_once_every_item_is_accepted()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sent = new List<string>();
        var service = new ClientCollectService(app.Get<IDbContextFactory<ClientCollectDb>>(), clock, new CapturingSender(sent));
        var email = Unique("client") + "@example.com";
        int RemindersToClient() => sent.Count(s => s.StartsWith(email + "|", StringComparison.Ordinal));

        var clientId = await service.CreateClientAsync("Acme", email);
        var (requestId, token) = await service.CreateRequestAsync(clientId, "Website content", null, null, [new NewItem(ItemKind.Text, "About us")]);

        await service.SendDueRemindersAsync();
        RemindersToClient().ShouldBe(0);   // too soon

        clock.Advance(TimeSpan.FromDays(4));
        await service.SendDueRemindersAsync();
        RemindersToClient().ShouldBe(1);

        // Answer and accept: the request completes and no further reminders go out.
        var itemId = (await service.GetPortalAsync(token))!.Value.Request.Items[0].Id;
        await service.SubmitTextAsync(token, itemId, "We build robots.");
        await service.ReviewAsync(itemId, accept: true, null);
        (await service.GetRequestAsync(requestId)).Request.Status.ShouldBe(RequestStatus.Complete);

        clock.Advance(TimeSpan.FromDays(4));
        await service.SendDueRemindersAsync();
        RemindersToClient().ShouldBe(1);
    }

    private sealed class CapturingSender(List<string> sent) : INotificationSender
    {
        public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
        {
            lock (sent)
                sent.Add($"{to}|{subject}|{body}");
            return Task.CompletedTask;
        }
    }
}
