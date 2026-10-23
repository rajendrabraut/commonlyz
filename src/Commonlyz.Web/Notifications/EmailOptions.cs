using System.ComponentModel.DataAnnotations;

namespace Commonlyz.Web.Notifications;

public enum SmtpSecurity
{
    /// <summary>STARTTLS on port 587 if offered, implicit TLS on 465. Recommended.</summary>
    Auto,

    /// <summary>Implicit TLS from the first byte (usually port 465).</summary>
    SslOnConnect,

    /// <summary>Require STARTTLS; fail if the server doesn't offer it (usually port 587).</summary>
    StartTls,

    /// <summary>Plain text. Only for local test servers such as Mailpit or smtp4dev.</summary>
    None,
}

/// <summary>
/// "Email" configuration section. When <see cref="Host"/> is empty, emails are still queued but only written
/// to the log, so development needs no mail server.
/// </summary>
public sealed class EmailOptions
{
    public const string Section = "Email";

    public string? Host { get; set; }

    [Range(1, 65535)]
    public int Port { get; set; } = 587;

    public SmtpSecurity Security { get; set; } = SmtpSecurity.Auto;

    public string? Username { get; set; }

    public string? Password { get; set; }

    [Required, EmailAddress]
    public string FromAddress { get; set; } = "no-reply@commonlyz.local";

    public string FromName { get; set; } = "Commonlyz";

    /// <summary>How often the dispatcher looks for queued emails.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);

    [Range(1, 500)]
    public int BatchSize { get; set; } = 20;

    /// <summary>After this many failed attempts an email is marked failed and no longer retried.</summary>
    [Range(1, 50)]
    public int MaxAttempts { get; set; } = 8;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Run the background dispatcher in this instance. Turn off on web-only nodes when a separate worker sends,
    /// or in tests that drive delivery explicitly. Emails are still queued either way.
    /// </summary>
    public bool DispatcherEnabled { get; set; } = true;

    public bool SmtpEnabled => !string.IsNullOrWhiteSpace(Host);
}
