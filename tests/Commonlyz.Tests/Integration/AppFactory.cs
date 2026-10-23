using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;

namespace Commonlyz.Tests.Integration;

/// <summary>Boots the whole platform (host + all 15 modules) against a throwaway SQL Server 2022 container.</summary>
public sealed partial class AppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminEmail = "admin@test.local";
    public const string AdminPassword = "Admin123!";

    /// <summary>
    /// Optional connection string to an already-running SQL Server (e.g. a local dev instance). When set, the
    /// tests use a uniquely named throwaway database on it instead of starting a container.
    /// </summary>
    private static readonly string? ExternalSql = Environment.GetEnvironmentVariable("COMMONLYZ_TEST_SQL");

    private readonly MsSqlContainer? _sql = ExternalSql is null
        ? new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build()
        : null;

    private readonly string? _externalDb = ExternalSql is null
        ? null
        : new SqlConnectionStringBuilder(ExternalSql) { InitialCatalog = $"commonlyz_test_{Guid.NewGuid():N}" }.ConnectionString;

    private string ConnectionString => _externalDb ?? _sql!.GetConnectionString();

    /// <summary>Real SMTP server the app sends to. Tests deliver explicitly via <see cref="DeliverEmailsAsync"/>.</summary>
    public FakeSmtpServer Smtp { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", ConnectionString);
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Admin:Email", AdminEmail);
        builder.UseSetting("Admin:Password", AdminPassword);
        builder.UseSetting("Email:Host", "127.0.0.1");
        builder.UseSetting("Email:Port", Smtp.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("Email:Security", "None");
        builder.UseSetting("Email:Username", FakeSmtpServer.Username);
        builder.UseSetting("Email:Password", FakeSmtpServer.Password);
        builder.UseSetting("Email:FromAddress", "no-reply@commonlyz.test");
        builder.UseSetting("Email:DispatcherEnabled", "false");
    }

    /// <summary>Runs dispatcher rounds until the outbox has nothing due.</summary>
    public async Task DeliverEmailsAsync()
    {
        var dispatcher = Get<Commonlyz.Web.Notifications.EmailDispatcher>();
        while (await dispatcher.ProcessBatchAsync(CancellationToken.None) > 0)
        {
        }
    }

    public async Task InitializeAsync()
    {
        if (_sql is not null)
        {
            await _sql.StartAsync();
        }

        _ = Services;   // force host start: runs every module's migrations and seeds the admin
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        if (_sql is not null)
        {
            await _sql.DisposeAsync();
        }
        else
        {
            await DropExternalDatabaseAsync();
        }

        await Smtp.DisposeAsync();
    }

    private async Task DropExternalDatabaseAsync()
    {
        var name = new SqlConnectionStringBuilder(_externalDb).InitialCatalog;
        SqlConnection.ClearAllPools();
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(_externalDb) { InitialCatalog = "master" }.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END";
        await command.ExecuteNonQueryAsync();
    }

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public HttpClient Anonymous() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>Signs in through the real login form (antiforgery token included) and returns a cookie-carrying client.</summary>
    public async Task<HttpClient> SignedInAsync()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var form = await client.GetStringAsync("/login");
        var token = TokenPattern().Match(form).Groups[1].Value;

        var response = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "login",
            ["__RequestVerificationToken"] = token,
            ["Input.Email"] = AdminEmail,
            ["Input.Password"] = AdminPassword,
            ["Input.RememberMe"] = "true",
        }));
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync());
        return client;
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();
}

[CollectionDefinition(Name)]
public sealed class AppTestGroup : ICollectionFixture<AppFactory>
{
    public const string Name = "app";
}
