using System.Globalization;
using Commonlyz.Core;
using Commonlyz.Core.Data;
using Commonlyz.Core.Modules;
using Commonlyz.Web;
using Commonlyz.Web.Components;
using Commonlyz.Web.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog((services, log) => log
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture));

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();

// ── Identity: users, roles, cookie auth ──────────────────────────────────────
builder.Services.AddDbContext<IdentityDb>((sp, options) => ModuleDatabaseExtensions.ConfigureSqlServer(options, sp, IdentityDb.Schema));
builder.Services.AddSingleton(new ModuleDatabase(nameof(IdentityDb), async (sp, ct) =>
{
    await using var scope = sp.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<IdentityDb>().Database.MigrateAsync(ct);
}));

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

builder.Services.AddIdentityCore<AppUser>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 8;
        options.Password.RequireNonAlphanumeric = false;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<IdentityDb>()
    .AddSignInManager()
    .AddDefaultTokenProviders()
    .AddClaimsPrincipalFactory<AppClaimsFactory>();

builder.Services.ConfigureApplicationCookie(cookie =>
{
    cookie.Cookie.Name = "commonlyz.auth";
    cookie.Cookie.SameSite = SameSiteMode.Lax;
    cookie.LoginPath = "/login";
    cookie.AccessDeniedPath = "/access-denied";
    cookie.ExpireTimeSpan = TimeSpan.FromDays(14);
    cookie.SlidingExpiration = true;
});

builder.Services.AddDataProtection().SetApplicationName("Commonlyz").PersistKeysToDbContext<IdentityDb>();
builder.Services.AddAuthorization();

// ── Shared services ──────────────────────────────────────────────────────────
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddSingleton<IUserDirectory, UserDirectory>();
builder.Services.AddSingleton<INotificationSender, LoggingNotificationSender>();
builder.Services.AddHealthChecks().AddDbContextCheck<IdentityDb>();

// ── Modules: each app registers its own DbContext (own schema) and services ───
foreach (var module in ModuleCatalog.All)
{
    builder.Services.AddSingleton(module);
    module.AddServices(builder.Services, builder.Configuration);
}

var app = builder.Build();

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    foreach (var database in app.Services.GetServices<ModuleDatabase>())
        await database.MigrateAsync(app.Services, CancellationToken.None);
}
await AdminSeeder.SeedAsync(app.Services, app.Configuration, app.Logger);

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/error", createScopeForErrors: true);

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseSerilogRequestLogging();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(ModuleCatalog.Assemblies);

app.MapPost("/logout", async (SignInManager<AppUser> signIn, [FromForm] string? returnUrl) =>
{
    await signIn.SignOutAsync();
    return TypedResults.LocalRedirect("~/login");
});
app.MapHealthChecks("/health");

foreach (var module in ModuleCatalog.All)
    module.MapEndpoints(app);

await app.RunAsync();

public partial class Program;
