using Microsoft.Extensions.Logging;

namespace Commonlyz.Core;

/// <summary>Default sender: writes notifications to the log. Replace with an SMTP/SendGrid implementation in the host.</summary>
public sealed partial class LoggingNotificationSender(ILogger<LoggingNotificationSender> logger) : INotificationSender
{
    public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        LogNotification(logger, to, subject);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification to {To}: {Subject}")]
    private static partial void LogNotification(ILogger logger, string to, string subject);
}
