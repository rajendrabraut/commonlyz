using Commonlyz.Core;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace Commonlyz.Web.Notifications;

/// <summary>
/// The app-wide <see cref="INotificationSender"/>: validates and queues the email in the outbox, then returns.
/// Delivery (and retries) happen in <see cref="EmailDispatcher"/>.
/// </summary>
public sealed class OutboxNotificationSender(IDbContextFactory<NotificationsDb> dbFactory, TimeProvider clock) : INotificationSender
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        var valid = MailboxAddress.TryParse(to, out var parsed) && parsed?.Address.Contains('@', StringComparison.Ordinal) == true;
        AppException.ThrowIf(!valid, $"'{to}' is not a valid email address.");

        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.Outbox.Add(new OutboxEmail
        {
            ToAddress = parsed!.Address,
            // Newlines in a subject are a header-injection vector; MimeKit encodes safely, but keep subjects single-line anyway.
            Subject = Clip(subject.ReplaceLineEndings(" ").Trim(), OutboxEmail.MaxSubjectLength),
            Body = Clip(body, OutboxEmail.MaxBodyLength),
            CreatedUtc = now,
            NextAttemptUtc = now,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];
}
