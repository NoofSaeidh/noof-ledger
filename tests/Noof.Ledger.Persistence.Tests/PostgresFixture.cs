using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Noof.Ledger.TestKit;

namespace Noof.Ledger.Persistence.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    // A List<string> here was a real defect, not a style point: Add runs from however many tests
    // xunit.runner.json lets run at once, List<T> is not thread safe, and a lost entry is a database
    // that never gets dropped. That is one of the two reasons 166 of them had accumulated.
    readonly ConcurrentBag<string> created = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async Task<string> CreateEmptyDatabaseConnectionStringAsync()
    {
        var name = $"noof_test_{Guid.NewGuid():N}";

        await DatabaseSettings.CreateEmptyDatabaseAsync(name, TestContext.Current.CancellationToken);

        created.Add(name);

        return WithoutPooling(DatabaseSettings.For(name));
    }

    internal async Task<LedgerDbContext> CreateContextAsync()
    {
        var connectionString = await CreateEmptyDatabaseConnectionStringAsync();

        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new LedgerDbContext(options);
    }

    // Every ordinary test that is not itself about migrating gets a context on a database that is
    // already at the latest schema, instead of re-running all 15+ migrations from an empty database.
    // The template is shared across worktrees and this fixture never migrates it - a stale template
    // (a worktree added a migration and forgot .\run.ps1 update-test-template) fails fast here rather
    // than silently testing against yesterday's schema.
    readonly Lazy<Task> templateFreshness = new(EnsureTemplateIsCurrentAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    internal async Task<LedgerDbContext> CreateMigratedContextAsync()
    {
        await templateFreshness.Value;

        var connectionString = await CreateDatabaseConnectionStringAsync();

        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new LedgerDbContext(options);
    }

    static async Task EnsureTemplateIsCurrentAsync()
    {
        var latestInAssembly = LatestMigrationInAssembly();
        var latestAppliedToTemplate = await LatestMigrationAppliedToTemplateAsync();

        TemplateFreshnessGuard.EnsureCurrent(latestInAssembly, latestAppliedToTemplate);
    }

    static string LatestMigrationInAssembly()
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none")
            .Options;
        using var db = new LedgerDbContext(options);
        return db.Database.GetMigrations().Last();
    }

    // Pooling must be off here: CREATE DATABASE ... TEMPLATE (STRATEGY FILE_COPY) fails with
    // "source database is being accessed by other users" against ANY live backend connected to the
    // template, including one this process itself only pooled rather than truly closed. Confirmed by
    // running this guard and then immediately cloning the template from the same process - a pooled
    // connection here made every subsequent CreateMigratedContextAsync call fail with Postgres error
    // 55006, every time, not just under contention.
    static async Task<string?> LatestMigrationAppliedToTemplateAsync()
    {
        var connectionString = WithoutPooling(DatabaseSettings.For(DatabaseSettings.TemplateDatabase));
        var options = new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new LedgerDbContext(options);
        var applied = await db.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken);
        return applied.LastOrDefault();
    }

    // NpgsqlConnection.ConnectionString drops the password once the connection has been opened
    // (confirmed empirically, not documented anywhere obvious) - a caller that needs the
    // connection string itself, to hand to something that opens its own connection (AddNoofPersistence,
    // a published host process), must capture it before opening, which CreateDatabaseAsync's open
    // NpgsqlConnection can no longer provide after the fact. This is that string, from the same
    // full-schema clone CreateDatabaseAsync itself opens.
    public async Task<string> CreateDatabaseConnectionStringAsync()
    {
        var name = $"noof_test_{Guid.NewGuid():N}";

        await DatabaseSettings.CreateDatabaseFromTemplateAsync(name, TestContext.Current.CancellationToken);

        created.Add(name);

        return WithoutPooling(DatabaseSettings.For(name));
    }

    public async Task<NpgsqlConnection> CreateDatabaseAsync()
    {
        var connectionString = await CreateDatabaseConnectionStringAsync();

        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    // Each of these connection strings names a database that exists for exactly one test, so
    // Npgsql's pool buys nothing - there is no second connection to reuse it. What it costs is
    // real: the physical connection Dispose() would normally return to that pool instead sits
    // open, keyed by a connection string nothing else will ever reuse, until DisposeAsync below
    // clears every pool at the very end of the whole "postgres" collection. With this collection
    // now creating a database per test, that is dozens of idle server connections stacking up for
    // the run's entire duration - confirmed via pg_stat_activity, which showed the count rising
    // in lockstep with distinct noof_test_* databases until Postgres's max_connections (100) was
    // exhausted and CREATE/OPEN calls started failing with 53300. Disabling pooling here makes
    // Dispose() close the socket immediately, so the connection count depends on what is running
    // concurrently, not on how many tests have run since the collection started.
    static string WithoutPooling(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();

        await using var admin = await DatabaseSettings.OpenAdminConnectionAsync(TestContext.Current.CancellationToken);

        List<string> undropped = [];

        foreach (var name in created)
        {
            // DROP DATABASE waits on a Postgres checkpoint before it can remove the files. With
            // dozens of throwaway databases created and dropped per run, that wait can exceed
            // Npgsql's default 30s command timeout under load - observed directly via
            // pg_stat_activity as wait_event = CheckpointDone, not a stuck or leaked connection.
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin)
            {
                CommandTimeout = DatabaseSettings.AdminCommandTimeoutSeconds,
            };

            // The second reason 166 databases had accumulated: this loop used to let a failed drop
            // escape, which skipped every remaining database in the list. One slow checkpoint cost
            // the whole run's cleanup. Now one failure costs exactly one database, and says so.
            try
            {
                await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            catch (Exception exposed) when (exposed is NpgsqlException or TimeoutException)
            {
                undropped.Add(name);
            }
        }

        if (undropped.Count > 0)
        {
            throw new InvalidOperationException(
                $"Left {undropped.Count} test database(s) behind: {string.Join(", ", undropped)}. "
                + "Run ops/clean-test-databases.ps1 to remove them.");
        }
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

internal static class TemplateFreshnessGuard
{
    public static void EnsureCurrent(string latestInAssembly, string? latestAppliedToTemplate)
    {
        if (latestAppliedToTemplate == latestInAssembly)
            return;

        throw new InvalidOperationException(
            $"noof_ledger_test_template is missing migration '{latestInAssembly}' "
            + $"(latest applied: '{latestAppliedToTemplate ?? "none"}'). "
            + "Run '.\\run.ps1 update-test-template' before running tests against the migrated template clone.");
    }
}
