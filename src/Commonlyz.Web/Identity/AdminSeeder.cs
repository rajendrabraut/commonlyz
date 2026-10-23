using Microsoft.AspNetCore.Identity;

namespace Commonlyz.Web.Identity;

/// <summary>Creates the Admin role and, on an empty database, the first admin from configuration (Admin:Email/Password).</summary>
public static partial class AdminSeeder
{
    public static async Task SeedAsync(IServiceProvider services, IConfiguration configuration, ILogger logger)
    {
        await using var scope = services.CreateAsyncScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        if (!await roles.RoleExistsAsync(Roles.Admin))
            Check(await roles.CreateAsync(new IdentityRole(Roles.Admin)));

        if (users.Users.Any())
            return;

        var email = configuration["Admin:Email"];
        var password = configuration["Admin:Password"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException(
                "The database has no users. Set Admin__Email and Admin__Password (environment) or Admin:Email/Admin:Password (config) to create the first admin.");

        var admin = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = configuration["Admin:DisplayName"] ?? "Administrator",
        };
        Check(await users.CreateAsync(admin, password));
        Check(await users.AddToRoleAsync(admin, Roles.Admin));
        LogSeeded(logger, email);
    }

    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException("Admin seeding failed: " + string.Join(" ", result.Errors.Select(e => e.Description)));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Created first admin user {Email}. Change the password after signing in.")]
    private static partial void LogSeeded(ILogger logger, string email);
}
