using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Commonlyz.Core.Data;

/// <summary>A module's DbContext, registered so the host can migrate every schema at startup.</summary>
public sealed record ModuleDatabase(string Name, Func<IServiceProvider, CancellationToken, Task> MigrateAsync);

public static class ModuleDatabaseExtensions
{
    public const string ConnectionStringName = "Default";

    /// <summary>
    /// Registers <typeparamref name="TContext"/> against the shared SQL Server database, isolated in its own
    /// <paramref name="schema"/> with its own migrations history table. Exposes <see cref="IDbContextFactory{TContext}"/>
    /// (the right lifetime for Blazor Server components) and a scoped context for design-time tooling.
    /// </summary>
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services, string schema)
        where TContext : DbContext
    {
        services.AddDbContextFactory<TContext>((sp, options) => ConfigureSqlServer(options, sp, schema));
        services.AddSingleton(new ModuleDatabase(typeof(TContext).Name, async (sp, ct) =>
        {
            await using var db = await sp.GetRequiredService<IDbContextFactory<TContext>>().CreateDbContextAsync(ct);
            await db.Database.MigrateAsync(ct);
        }));
        return services;
    }

    public static void ConfigureSqlServer(DbContextOptionsBuilder options, IServiceProvider sp, string schema) =>
        options
            .UseSqlServer(
                // Read lazily so test hosts can override configuration.
                sp.GetRequiredService<IConfiguration>().GetConnectionString(ConnectionStringName)
                    ?? throw new InvalidOperationException($"ConnectionStrings:{ConnectionStringName} is not configured."),
                sql => sql.EnableRetryOnFailure().MigrationsHistoryTable("__EFMigrationsHistory", schema))
            // Unique-index conflicts are expected and handled by services; unhandled errors are still logged by the host.
            .ConfigureWarnings(w => w.Log(
                (CoreEventId.SaveChangesFailed, LogLevel.Debug),
                (RelationalEventId.CommandError, LogLevel.Debug)));

    // 2601: duplicate key in unique index; 2627: unique/primary key constraint violation.
    public static bool IsUniqueViolation(this DbUpdateException ex) =>
        ex.GetBaseException() is SqlException { Number: 2601 or 2627 };
}
