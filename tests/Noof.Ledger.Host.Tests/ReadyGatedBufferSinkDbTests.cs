using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.TestKit;
using Npgsql;

namespace Noof.Ledger.Host.Tests;

[Collection("app-log-sink")]
public sealed class ReadyGatedBufferSinkDbTests
{
    [Fact]
    public async Task An_event_logged_before_the_gate_is_Ready_still_reaches_app_log_once_it_is()
    {
        if (!await DatabaseIsReachableAsync(TestContext.Current.CancellationToken))
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var databaseName = $"noof_ready_gated_sink_{Guid.NewGuid():N}";
        await CreateCloneAsync(databaseName, TestContext.Current.CancellationToken);
        var connectionString = new NpgsqlConnectionStringBuilder(DatabaseSettings.AdminConnectionString) { Database = databaseName }.ConnectionString;

        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseTempLogDirectory();
                builder.UseSetting("ConnectionStrings:Ledger", connectionString);
                builder.UseSetting("Database:MigrateOnStartup", "true");
                builder.UseSetting("Backup:Enabled", "false");
                builder.ConfigureServices(FakeUserStore.Register);
            });

            using var client = factory.CreateClient();

            var gate = factory.Services.GetRequiredService<IDatabaseGate>();
            var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Noof.Ledger.Host.Tests.PreReadyProbe");
            var marker = $"pre-ready-{Guid.NewGuid():N}";

            // Logged straight after the host builds, racing the gate's own migration - the buffer
            // sink's job is to make this land in app_log regardless of which side of Ready it fell on.
            logger.LogWarning("{Marker}", marker);

            await gate.WaitUntilReadyAsync(TestContext.Current.CancellationToken);

            var row = await PollForRowAsync(connectionString, marker, TestContext.Current.CancellationToken);

            row.Should().NotBeNull("the pre-Ready event must be flushed into app_log once the gate turns Ready");
        }
        finally
        {
            await DropCloneAsync(databaseName);
        }
    }

    static async Task<string?> PollForRowAsync(string connectionString, string marker, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT message FROM app_log WHERE message = @marker", connection);
            command.Parameters.AddWithValue("marker", marker);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
                return reader.GetString(0);

            await reader.DisposeAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        return null;
    }

    static async Task<bool> DatabaseIsReachableAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new NpgsqlConnection(DatabaseSettings.AdminConnectionString);
            await connection.OpenAsync(cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    static async Task CreateCloneAsync(string name, CancellationToken cancellationToken)
    {
        await using var admin = new NpgsqlConnection(DatabaseSettings.AdminConnectionString);
        await admin.OpenAsync(cancellationToken);
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\" TEMPLATE {DatabaseSettings.TemplateDatabase}", admin);
        await create.ExecuteNonQueryAsync(cancellationToken);
    }

    static async Task DropCloneAsync(string name)
    {
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(DatabaseSettings.AdminConnectionString);
        await admin.OpenAsync(CancellationToken.None);
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin) { CommandTimeout = 120 };
        await drop.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
