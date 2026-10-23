using System.Security.Claims;
using Commonlyz.Core;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Commonlyz.Web.Identity;

/// <summary>Works in both Blazor circuits (AuthenticationStateProvider) and plain HTTP endpoints (HttpContext).</summary>
public sealed class CurrentUser(IServiceProvider services, IHttpContextAccessor http) : ICurrentUser
{
    public async Task<AppUserInfo> GetAsync()
    {
        var principal = http.HttpContext?.User is { Identity.IsAuthenticated: true } httpUser
            ? httpUser
            : (await services.GetRequiredService<AuthenticationStateProvider>().GetAuthenticationStateAsync()).User;

        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new AppException("You must be signed in.");

        return new AppUserInfo(
            id,
            principal.FindFirstValue(AppClaimsFactory.DisplayNameClaim) ?? principal.Identity?.Name ?? "",
            principal.FindFirstValue(ClaimTypes.Email) ?? principal.Identity?.Name ?? "",
            principal.IsInRole(Roles.Admin));
    }
}

public sealed class UserDirectory(IServiceScopeFactory scopes) : IUserDirectory
{
    public async Task<IReadOnlyList<AppUserInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        // Own scope: a Blazor circuit's long-lived UserManager context would otherwise serve stale data.
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDb>();

        var adminIds = await (from ur in db.UserRoles
                              join r in db.Roles on ur.RoleId equals r.Id
                              where r.Name == Roles.Admin
                              select ur.UserId).ToListAsync(cancellationToken);

        var users = await db.Users.AsNoTracking().OrderBy(u => u.DisplayName).ToListAsync(cancellationToken);
        return [.. users.Select(u => new AppUserInfo(u.Id, u.DisplayName, u.Email ?? "", adminIds.Contains(u.Id)))];
    }
}
