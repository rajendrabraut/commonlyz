using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Commonlyz.Web.Identity;

/// <summary>Adds the display name to the auth cookie so pages don't need a DB round-trip to show it.</summary>
public sealed class AppClaimsFactory(
    UserManager<AppUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<AppUser, IdentityRole>(userManager, roleManager, options)
{
    public const string DisplayNameClaim = "display_name";

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(DisplayNameClaim, string.IsNullOrWhiteSpace(user.DisplayName) ? user.Email ?? user.UserName ?? "" : user.DisplayName));
        return identity;
    }
}
