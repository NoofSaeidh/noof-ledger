using Microsoft.EntityFrameworkCore;
using Npgsql;
using Noof.TestKit;

namespace Noof.Persistence.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    readonly List<string> created = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async Task<NoofDbContext> CreateContextAsync()
    {
        var name = $"noof_test_{Guid.NewGuid():N}";

        await using (var admin = new NpgsqlConnection(DatabaseSettings.AdminConnectionString))
        {
            await admin.OpenAsync(TestContext.Current.CancellationToken);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        created.Add(name);

        var options = new DbContextOptionsBuilder<NoofDbContext>()
            .UseNpgsql(DatabaseSettings.For(name))
            .Options;

        return new NoofDbContext(options);
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
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
