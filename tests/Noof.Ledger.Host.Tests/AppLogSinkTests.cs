using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.TestKit;
using Npgsql;

namespace Noof.Ledger.Host.Tests;

[Collection("app-log-sink")]
[Trait("Category", "Database")]
public sealed class AppLogSinkTests
{
    [Fact]
    public async Task An_event_logged_with_a_transaction_scope_and_a_stage_property_lands_in_app_log_in_the_contract_row_format()
    {
        if (!await DatabaseIsReachableAsync(TestContext.Current.CancellationToken))
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var databaseName = $"noof_app_log_sink_{Guid.NewGuid():N}";
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
            await gate.WaitUntilReadyAsync(TestContext.Current.CancellationToken);

            var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Noof.Ledger.Host.Tests.Probe");
            var transactionId = Guid.NewGuid();
            using (TransactionLogScope.Begin(logger, transactionId))
            {
                logger.LogInformation(TransactionStages.ReceivedEventId, "{Stage:l}", TransactionStages.Received);
            }

            // The Postgres sink batches on a timer (default period ~2s); poll briefly rather than
            // sleep a fixed guess, since the exact flush timing is Serilog's, not this test's.
            var row = await PollForRowAsync(connectionString, transactionId, TestContext.Current.CancellationToken);

            row.Should().NotBeNull();
            row!.Value.Source.Should().Be("Noof.Ledger.Host.Tests.Probe");
            row.Value.Message.Should().Be(TransactionStages.Received);
            row.Value.TransactionId.Should().Be(transactionId);
            // Postgres's jsonb type re-serializes on the way out with its own canonical spacing -
            // a space after every ':' and ',' - regardless of how the JSON was written in, so the
            // read-back text never matches whatever exact bytes the sink sent.
            row.Value.PropertiesJson.Should().Contain($"\"Stage\": \"{TransactionStages.Received}\"");
            row.Value.PropertiesJson.Should().Contain($"\"EventId\": {{\"Id\": {TransactionStages.ReceivedEventId}");
        }
        finally
        {
            await DropCloneAsync(databaseName);
        }
    }

    static async Task<(string? Source, string Message, Guid? TransactionId, string PropertiesJson)?> PollForRowAsync(
        string connectionString, Guid transactionId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                "SELECT source, message, transaction_id, properties::text FROM app_log WHERE transaction_id = @id", connection);
            command.Parameters.AddWithValue("id", transactionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return (
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetGuid(2),
                    reader.GetString(3));
            }

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
