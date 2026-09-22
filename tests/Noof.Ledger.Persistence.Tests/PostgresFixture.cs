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

        await using (var admin = new NpgsqlConnection(DatabaseSettings.AdminConnectionString))
        {
            await admin.OpenAsync(TestContext.Current.CancellationToken);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        created.Add(name);

        return DatabaseSettings.For(name);
    }

    internal async Task<LedgerDbContext> CreateContextAsync()
    {
        var connectionString = await CreateEmptyDatabaseConnectionStringAsync();

        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new LedgerDbContext(options);
    }

    public async Task<NpgsqlConnection> CreateDatabaseAsync()
    {
        var name = $"noof_test_{Guid.NewGuid():N}";

        await using (var admin = new NpgsqlConnection(DatabaseSettings.AdminConnectionString))
        {
            await admin.OpenAsync(TestContext.Current.CancellationToken);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\" TEMPLATE {DatabaseSettings.TemplateDatabase}", admin);
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        created.Add(name);

        var connection = new NpgsqlConnection(DatabaseSettings.For(name));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();

        await using var admin = new NpgsqlConnection(DatabaseSettings.AdminConnectionString);
        await admin.OpenAsync(TestContext.Current.CancellationToken);

        List<string> undropped = [];

        foreach (var name in created)
        {
            // DROP DATABASE waits on a Postgres checkpoint before it can remove the files. With
            // dozens of throwaway databases created and dropped per run, that wait can exceed
            // Npgsql's default 30s command timeout under load - observed directly via
            // pg_stat_activity as wait_event = CheckpointDone, not a stuck or leaked connection.
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin)
            {
                CommandTimeout = 120,
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
