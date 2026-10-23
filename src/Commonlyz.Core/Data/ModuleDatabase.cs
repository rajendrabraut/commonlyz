using System.Data;
using System.Data.Common;
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

    /// <summary>
    /// Runs <paramref name="work"/> in a transaction inside the retrying execution strategy (required when
    /// EnableRetryOnFailure is on). On a transient failure the whole unit is retried with a clean change tracker.
    /// </summary>
    public static Task InTransactionAsync(this DbContext db, Func<Task> work,
        IsolationLevel isolation = IsolationLevel.ReadCommitted, CancellationToken cancellationToken = default) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(isolation, cancellationToken);
            try
            {
                await work();
            }
            catch
            {
                await RestoreDefaultIsolationAsync(db, isolation);
                throw;
            }
            await RestoreDefaultIsolationAsync(db, isolation);
            await tx.CommitAsync(cancellationToken);
        });

    /// <summary>
    /// SQL Server keeps a session's isolation level when the connection goes back to the pool, so after a
    /// SERIALIZABLE transaction the next unrelated query on that connection would silently run SERIALIZABLE too.
    /// Must run before commit: EF closes (returns to the pool) a connection it opened for a transaction as soon
    /// as the transaction ends. Locks already taken stay held until commit, so this doesn't weaken the transaction.
    /// </summary>
    private static async Task RestoreDefaultIsolationAsync(DbContext db, IsolationLevel isolation)
    {
        if (isolation == IsolationLevel.ReadCommitted)
            return;
        try
        {
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION ISOLATION LEVEL READ COMMITTED", CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            // Connection already broken: it won't be reused from the pool in that state anyway.
        }
    }

    // 2601: duplicate key in unique index; 2627: unique/primary key constraint violation.
    public static bool IsUniqueViolation(this DbUpdateException ex) =>
        ex.GetBaseException() is SqlException { Number: 2601 or 2627 };
}
