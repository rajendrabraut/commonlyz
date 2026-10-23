using Commonlyz.Core;
using Commonlyz.Modules.SlotBook;
using Commonlyz.Web.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Commonlyz.Tests.Integration;

[Collection(AppTestGroup.Name)]
public class EmailTests(AppFactory app)
{
    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    [Fact]
    public async Task Queued_email_is_delivered_over_authenticated_SMTP_and_marked_sent()
    {
        var to = Unique("user") + "@example.com";
        await app.Get<INotificationSender>().SendAsync(to, "Hello\r\nBcc: attacker@evil.test", "Body text");

        await app.DeliverEmailsAsync();

        var message = app.Smtp.Find(m => m.To.Mailboxes.Any(a => a.Address == to)).ShouldNotBeNull();
        message.Subject.ShouldBe("Hello Bcc: attacker@evil.test");   // header injection neutralised: still one subject line
        message.Bcc.ShouldBeEmpty();
        message.From.Mailboxes.Single().Address.ShouldBe("no-reply@commonlyz.test");
        message.TextBody.ShouldNotBeNull().ShouldContain("Body text");

        await using var db = await app.Get<IDbContextFactory<NotificationsDb>>().CreateDbContextAsync();
        var row = await db.Outbox.SingleAsync(o => o.ToAddress == to);
        row.SentUtc.ShouldNotBeNull();
        row.Attempts.ShouldBe(1);
        message.MessageId.ShouldBe($"outbox-{row.Id}@commonlyz.test");
    }

    [Theory]
    [InlineData("not an email")]
    [InlineData("")]
    [InlineData("@example.com")]
    public async Task Invalid_addresses_are_rejected_before_queueing(string to) =>
        await Should.ThrowAsync<AppException>(() => app.Get<INotificationSender>().SendAsync(to, "s", "b"));

    [Fact]
    public async Task Booking_confirmation_email_contains_the_cancel_link()
    {
        var slots = app.Get<SlotBookService>();
        var slug = Unique("mail");
        var typeId = await slots.CreateTypeAsync("Intro call", slug, 30, "UTC");
        await slots.UpdateTypeAsync(typeId, "Intro call", slug, null, "Video call", 30, 0, 0, 30, "UTC", true);
        await slots.SetRulesAsync(typeId, [.. Enum.GetValues<DayOfWeek>().Select(d => (d, new TimeOnly(0, 0), new TimeOnly(23, 30)))]);
        var type = (await slots.GetPublicTypeAsync(slug))!;
        var slot = (await slots.GetSlotsAsync(type, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1), 1)).First();
        var guest = Unique("guest") + "@example.com";

        var booking = await slots.BookAsync(slug, slot.StartUtc, "Ada", guest, null, "https://commonlyz.test/");
        await app.DeliverEmailsAsync();

        var message = app.Smtp.Find(m => m.To.Mailboxes.Any(a => a.Address == guest)).ShouldNotBeNull();
        message.Subject.ShouldStartWith("Booked: Intro call");
        message.TextBody.ShouldNotBeNull().ShouldContain($"https://commonlyz.test/book/cancel/{booking.CancelToken}");
        message.TextBody.ShouldNotBeNull().ShouldContain("Where: Video call");
    }

    [Fact]
    public async Task Failures_back_off_exponentially_then_give_up_and_can_be_retried()
    {
        await app.DeliverEmailsAsync();   // leave nothing else pending for the dispatcher below to claim

        // Far-future clock: these rows are invisible to anything running on the real clock.
        var clock = new FakeTimeProvider(new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var dbFactory = app.Get<IDbContextFactory<NotificationsDb>>();
        var to = Unique("fail") + "@example.com";
        await new OutboxNotificationSender(dbFactory, clock).SendAsync(to, "Will fail", "x");

        var options = Options.Create(new EmailOptions { MaxAttempts = 3, BatchSize = 50 });
        var dispatcher = new EmailDispatcher(dbFactory, new FailingTransport(), options, clock, NullLogger<EmailDispatcher>.Instance);
        async Task<OutboxEmail> RowAsync()
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            return await db.Outbox.AsNoTracking().SingleAsync(o => o.ToAddress == to);
        }

        (await dispatcher.ProcessBatchAsync(default)).ShouldBe(1);
        var row = await RowAsync();
        (row.Attempts, row.FailedUtc, row.LastError).ShouldBe((1, null, "SMTP is down"));
        row.NextAttemptUtc.ShouldBe(clock.GetUtcNow().UtcDateTime.AddSeconds(30));

        (await dispatcher.ProcessBatchAsync(default)).ShouldBe(0);   // not due yet
        clock.Advance(TimeSpan.FromSeconds(30));
        (await dispatcher.ProcessBatchAsync(default)).ShouldBe(1);
        (await RowAsync()).NextAttemptUtc.ShouldBe(clock.GetUtcNow().UtcDateTime.AddSeconds(60));   // doubled

        clock.Advance(TimeSpan.FromSeconds(60));
        await dispatcher.ProcessBatchAsync(default);
        row = await RowAsync();
        row.Attempts.ShouldBe(3);
        row.FailedUtc.ShouldNotBeNull();   // gave up after MaxAttempts
        row.Status.ShouldBe("Failed");

        await new EmailOutbox(dbFactory, clock).RetryAsync(row.Id);
        row = await RowAsync();
        (row.Attempts, row.FailedUtc, row.Status).ShouldBe((0, null, "Queued"));
        await using (var db = await dbFactory.CreateDbContextAsync())
            await db.Outbox.Where(o => o.Id == row.Id).ExecuteDeleteAsync();   // don't leave it for other tests
    }

    [Fact]
    public void Backoff_doubles_and_caps_at_one_hour()
    {
        EmailDispatcher.Backoff(1).ShouldBe(TimeSpan.FromSeconds(30));
        EmailDispatcher.Backoff(2).ShouldBe(TimeSpan.FromMinutes(1));
        EmailDispatcher.Backoff(5).ShouldBe(TimeSpan.FromMinutes(8));
        EmailDispatcher.Backoff(20).ShouldBe(TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task Wrong_SMTP_credentials_fail_the_batch_without_losing_emails()
    {
        await app.DeliverEmailsAsync();
        var clock = new FakeTimeProvider(new DateTimeOffset(2101, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var dbFactory = app.Get<IDbContextFactory<NotificationsDb>>();
        var to = Unique("auth") + "@example.com";
        await new OutboxNotificationSender(dbFactory, clock).SendAsync(to, "Auth test", "x");

        var badLogin = Options.Create(new EmailOptions
        {
            Host = "127.0.0.1", Port = app.Smtp.Port, Security = SmtpSecurity.None,
            Username = FakeSmtpServer.Username, Password = "wrong", FromAddress = "no-reply@commonlyz.test",
        });
        var dispatcher = new EmailDispatcher(dbFactory, new SmtpEmailTransport(badLogin), badLogin, clock, NullLogger<EmailDispatcher>.Instance);
        await dispatcher.ProcessBatchAsync(default);

        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.Outbox.SingleAsync(o => o.ToAddress == to);
        row.SentUtc.ShouldBeNull();
        row.Attempts.ShouldBe(1);
        row.LastError.ShouldNotBeNullOrWhiteSpace();
        app.Smtp.Find(m => m.To.Mailboxes.Any(a => a.Address == to)).ShouldBeNull();
        await db.Outbox.Where(o => o.Id == row.Id).ExecuteDeleteAsync();
    }

    private sealed class FailingTransport : IEmailTransport
    {
        public string Description => "always fails";

        public Task<IReadOnlyDictionary<long, Exception>> SendBatchAsync(IReadOnlyList<OutboxEmail> emails, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<long, Exception>>(emails.ToDictionary(e => e.Id, _ => (Exception)new InvalidOperationException("SMTP is down")));
    }
}
