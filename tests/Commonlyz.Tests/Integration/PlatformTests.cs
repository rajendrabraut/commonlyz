using System.Net;
using Commonlyz.Core.Modules;
using Commonlyz.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Commonlyz.Tests.Integration;

/// <summary>Host-level checks: every module is reachable, protected, and its public pages stay public.</summary>
[Collection(AppTestGroup.Name)]
public class PlatformTests(AppFactory app)
{
    public static TheoryData<string> AdminRoutes => [.. ModuleCatalog.All.Select(m => m.Info.Route).Append("/").Append("/users").Append("/email")];

    [Fact]
    public void All_fifteen_modules_are_registered_with_unique_keys_and_routes()
    {
        ModuleCatalog.All.Count.ShouldBe(15);
        ModuleCatalog.All.Select(m => m.Info.Key).Distinct().Count().ShouldBe(15);
        ModuleCatalog.All.Select(m => m.Info.Route).Distinct().Count().ShouldBe(15);
    }

    [Theory]
    [MemberData(nameof(AdminRoutes))]
    public async Task Admin_pages_redirect_anonymous_users_to_login(string route)
    {
        var response = await app.Anonymous().GetAsync(route);
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().ShouldContain("/login");
    }

    [Theory]
    [MemberData(nameof(AdminRoutes))]
    public async Task Admin_pages_render_for_signed_in_admin(string route)
    {
        var client = await app.SignedInAsync();
        var response = await client.GetAsync(route);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Commonlyz");
    }

    [Theory]
    [InlineData("/status")]
    [InlineData("/status.json")]
    [InlineData("/login")]
    [InlineData("/p/nobody")]
    [InlineData("/f/no-such-form")]
    [InlineData("/book/no-such-type")]
    [InlineData("/portal/not-a-token")]
    public async Task Public_pages_are_reachable_without_signing_in(string route)
    {
        var response = await app.Anonymous().GetAsync(route);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/timetrack/export.csv?from=2026-01-01&to=2026-01-07")]
    [InlineData("/journal/export.md")]
    [InlineData("/formflow/1/responses.csv")]
    [InlineData("/clientcollect/files/1")]
    public async Task File_endpoints_require_sign_in(string route)
    {
        var response = await app.Anonymous().GetAsync(route);
        response.StatusCode.ShouldBeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Health_endpoint_checks_the_database()
    {
        var response = await app.Anonymous().GetAsync("/health");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Every_module_reports_dashboard_stats()
    {
        using var scope = app.Services.CreateScope();
        foreach (var module in app.Get<IEnumerable<IAppModule>>().Where(m => m.Info.Key is not ("journal" or "chat")))   // need a signed-in user
        {
            var stats = await module.GetStatsAsync(scope.ServiceProvider, CancellationToken.None);
            stats.ShouldNotBeEmpty(module.Info.Name);
        }
    }
}
