using Microsoft.EntityFrameworkCore;
using Npgsql;
using Noof.Ledger.TestKit;

namespace Noof.Ledger.Persistence.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    readonly List<string> created = [];

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

    public async Task<LedgerDbContext> CreateContextAsync()
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
            await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
