using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Commonlyz.Web.Notifications;

/// <summary>Delivers emails for the <see cref="EmailDispatcher"/>.</summary>
public interface IEmailTransport
{
    string Description { get; }

    /// <summary>Sends the batch. Never throws for delivery problems: returns the emails that failed, to retry later.</summary>
    Task<IReadOnlyDictionary<long, Exception>> SendBatchAsync(IReadOnlyList<OutboxEmail> emails, CancellationToken cancellationToken);
}

/// <summary>
/// SMTP via MailKit (System.Net.Mail.SmtpClient is no longer recommended by Microsoft). One connection per batch;
/// the server certificate is validated by default.
/// </summary>
public sealed class SmtpEmailTransport(IOptions<EmailOptions> options) : IEmailTransport
{
    private readonly EmailOptions _options = options.Value;

    public string Description => $"SMTP {_options.Host}:{_options.Port} ({_options.Security}) from {_options.FromAddress}";

    public async Task<IReadOnlyDictionary<long, Exception>> SendBatchAsync(IReadOnlyList<OutboxEmail> emails, CancellationToken cancellationToken)
    {
        using var client = new SmtpClient { Timeout = (int)_options.Timeout.TotalMilliseconds };
        try
        {
            // Only constructed when Host is set (see AddEmailNotifications).
            await client.ConnectAsync(_options.Host!, _options.Port, ToMailKit(_options.Security), cancellationToken);
            if (!string.IsNullOrEmpty(_options.Username))
                await client.AuthenticateAsync(_options.Username, _options.Password ?? "", cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unreachable server or bad credentials: the whole batch is retried later.
            return emails.ToDictionary(e => e.Id, _ => ex);
        }

        var failures = new Dictionary<long, Exception>();
        for (var i = 0; i < emails.Count; i++)
        {
            try
            {
                await client.SendAsync(ToMime(emails[i]), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures[emails[i].Id] = ex;
                if (!client.IsConnected)
                {
                    // Connection lost: everything not yet sent in this batch failed too.
                    foreach (var rest in emails.Skip(i + 1))
                        failures[rest.Id] = ex;
                    break;
                }
            }
        }

        if (client.IsConnected)
            await client.DisconnectAsync(quit: true, cancellationToken);
        return failures;
    }

    private static SecureSocketOptions ToMailKit(SmtpSecurity security) => security switch
    {
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurity.None => SecureSocketOptions.None,
        _ => SecureSocketOptions.Auto,
    };

    private MimeMessage ToMime(OutboxEmail email)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
        message.To.Add(MailboxAddress.Parse(email.ToAddress));
        message.Subject = email.Subject;
        // Stable Message-Id per outbox row: if a retry re-sends after an ambiguous failure, mail clients can de-duplicate.
        message.MessageId = $"outbox-{email.Id}@{_options.FromAddress.Split('@')[^1]}";
        message.Body = new TextPart("plain") { Text = email.Body };
        return message;
    }
}

/// <summary>Used when no SMTP host is configured: "delivers" by writing to the log, so dev needs no mail server.</summary>
public sealed partial class LogEmailTransport(ILogger<LogEmailTransport> logger) : IEmailTransport
{
    public string Description => "SMTP not configured — emails are written to the application log";

    public Task<IReadOnlyDictionary<long, Exception>> SendBatchAsync(IReadOnlyList<OutboxEmail> emails, CancellationToken cancellationToken)
    {
        foreach (var email in emails)
            LogEmail(logger, email.ToAddress, email.Subject);
        return Task.FromResult<IReadOnlyDictionary<long, Exception>>(new Dictionary<long, Exception>());
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Email to {To}: {Subject} (SMTP not configured; set Email:Host to send)")]
    private static partial void LogEmail(ILogger logger, string to, string subject);
}
