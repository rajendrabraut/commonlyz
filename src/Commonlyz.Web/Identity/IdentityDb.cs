using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Commonlyz.Web.Identity;

public sealed class AppUser : IdentityUser
{
    public const int MaxDisplayNameLength = 100;

    public string DisplayName { get; set; } = "";
}

/// <summary>Users, roles and Data Protection keys (so auth cookies survive restarts and work across instances).</summary>
public sealed class IdentityDb(DbContextOptions<IdentityDb> options)
    : IdentityDbContext<AppUser>(options), IDataProtectionKeyContext
{
    public const string Schema = "identity";

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);
        builder.Entity<AppUser>().Property(u => u.DisplayName).HasMaxLength(AppUser.MaxDisplayNameLength);
    }
}

public static class Roles
{
    public const string Admin = "Admin";
}
