namespace Commonlyz.Core;

public sealed record AppUserInfo(string Id, string DisplayName, string Email, bool IsAdmin);

/// <summary>The signed-in user, from whichever context is active (Blazor circuit or HTTP request).</summary>
public interface ICurrentUser
{
    Task<AppUserInfo> GetAsync();
}

public interface IUserDirectory
{
    Task<IReadOnlyList<AppUserInfo>> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Outbound email. The host's implementation queues the message in a database outbox and returns quickly;
/// a background dispatcher delivers it over SMTP with retries, so callers never wait on a mail server.
/// </summary>
public interface INotificationSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default);
}
