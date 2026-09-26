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
public sealed class DatabaseLogLevelDbTests
{
    [Fact]
    public async Task Setting_Debug_lets_a_Debug_event_reach_app_log_with_its_transaction_id()
    {
        if (!await DatabaseIsReachableAsync(TestContext.Current.CancellationToken))
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var databaseName = $"noof_db_log_level_{Guid.NewGuid():N}";
        await CreateCloneAsync(databaseName, TestContext.Current.CancellationToken);
        var connectionString = ConnectionStringFor(databaseName);

        try
        {
            await using var factory = BuildFactory(connectionString);
            using var client = factory.CreateClient();
            var gate = factory.Services.GetRequiredService<IDatabaseGate>();
            await gate.WaitUntilReadyAsync(TestContext.Current.CancellationToken);

            var databaseLogLevel = factory.Services.GetRequiredService<IDatabaseLogLevel>();
            await databaseLogLevel.SetAsync(LogSeverity.Debug, TestContext.Current.CancellationToken);

            var loggerFactory = factory.Services.GetRequiredService<ILoggerFactory>();
            var loggerA = loggerFactory.CreateLogger("Noof.Ledger.Host.Tests.ProbeA");
            var loggerB = loggerFactory.CreateLogger("Noof.Ledger.Host.Tests.ProbeB");
            var transactionId = Guid.NewGuid();
            var marker = $"debug-tx-{Guid.NewGuid():N}";

            using (TransactionLogScope.Begin(loggerA, transactionId))
                loggerB.LogDebug("{Marker}", marker);

            var row = await PollForRowAsync(
                factory, filter => filter with { MinLevel = LogSeverity.Verbose, TransactionId = transactionId },
                row => row.Message.Contains(marker, StringComparison.Ordinal),
                TestContext.Current.CancellationToken);

            row.Should().NotBeNull("a Debug event logged while the database level is Debug must reach app_log with the ambient transaction id");
        }
        finally
        {
            await DropCloneAsync(databaseName);
        }
    }

    [Fact]
    public async Task Setting_Information_keeps_a_new_Debug_event_out_of_app_log()
    {
        if (!await DatabaseIsReachableAsync(TestContext.Current.CancellationToken))
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var databaseName = $"noof_db_log_level_{Guid.NewGuid():N}";
        await CreateCloneAsync(databaseName, TestContext.Current.CancellationToken);
        var connectionString = ConnectionStringFor(databaseName);

        try
        {
            await using var factory = BuildFactory(connectionString);
            using var client = factory.CreateClient();
            var gate = factory.Services.GetRequiredService<IDatabaseGate>();
            await gate.WaitUntilReadyAsync(TestContext.Current.CancellationToken);

            var databaseLogLevel = factory.Services.GetRequiredService<IDatabaseLogLevel>();
            await databaseLogLevel.SetAsync(LogSeverity.Information, TestContext.Current.CancellationToken);

            var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Noof.Ledger.Host.Tests.InformationFloorProbe");
            var debugMarker = $"debug-{Guid.NewGuid():N}";
            var warningMarker = $"warning-{Guid.NewGuid():N}";

            logger.LogDebug("{Marker}", debugMarker);
            logger.LogWarning("{Marker}", warningMarker);

            var barrier = await PollForRowAsync(
                factory, filter => filter with { MinLevel = LogSeverity.Warning },
                row => row.Message.Contains(warningMarker, StringComparison.Ordinal),
                TestContext.Current.CancellationToken);
            barrier.Should().NotBeNull("the Warning barrier must arrive before the absence check means anything");

            var debugRow = await PollForRowAsync(
                factory, filter => filter with { MinLevel = LogSeverity.Verbose },
                row => row.Message.Contains(debugMarker, StringComparison.Ordinal),
                TestContext.Current.CancellationToken, attempts: 1);
            debugRow.Should().BeNull("the database floor is Information, so the Debug event never cleared it");
        }
        finally
        {
            await DropCloneAsync(databaseName);
        }
    }

    [Fact]
    public async Task The_stored_level_is_reapplied_after_a_restart()
    {
        if (!await DatabaseIsReachableAsync(TestContext.Current.CancellationToken))
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var databaseName = $"noof_db_log_level_{Guid.NewGuid():N}";
        await CreateCloneAsync(databaseName, TestContext.Current.CancellationToken);
        var connectionString = ConnectionStringFor(databaseName);

        try
        {
            await using (var factory = BuildFactory(connectionString))
            {
                using var client = factory.CreateClient();
                var gate = factory.Services.GetRequiredService<IDatabaseGate>();
                await gate.WaitUntilReadyAsync(TestContext.Current.CancellationToken);

                await factory.Services.GetRequiredService<IDatabaseLogLevel>()
                    .SetAsync(LogSeverity.Debug, TestContext.Current.CancellationToken);
            }

            await using var restarted = BuildFactory(connectionString);
            using var restartedClient = restarted.CreateClient();
            var restartedGate = restarted.Services.GetRequiredService<IDatabaseGate>();
            await restartedGate.WaitUntilReadyAsync(TestContext.Current.CancellationToken);

            var databaseLogLevel = restarted.Services.GetRequiredService<IDatabaseLogLevel>();
            LogSeverity? observed = null;

            for (var attempt = 0; attempt < 20 && observed != LogSeverity.Debug; attempt++)
            {
                observed = databaseLogLevel.Current;
                if (observed == LogSeverity.Debug)
                    break;

                await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
            }

            observed.Should().Be(LogSeverity.Debug, "the loader must apply the stored level once the gate is Ready on the new host");
        }
        finally
        {
            await DropCloneAsync(databaseName);
        }
    }

    // V9 finding (operator question 7): checked once, deliberately not kept as a test. At the
    // Debug database level, after a DbContext round trip (ILogQuery.QueryAsync) followed by a
    // Warning flush barrier, zero app_log rows had a source starting with "Npgsql" - so Npgsql does
    // not log through the app's ILoggerFactory here, and no Serilog:MinimumLevel:Override:Npgsql
    // entry is needed. A test asserting "0 rows" would never have gone red, which CLAUDE.md's
    // Testing section calls out by name as enforcing nothing; recorded in docs/BACKLOG.md instead
    // (V12) so a future EF/Npgsql upgrade that changes this is a decision, not a silent flood.

    static WebApplicationFactory<Program> BuildFactory(string connectionString) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseTempLogDirectory();
            builder.UseSetting("ConnectionStrings:Ledger", connectionString);
            builder.UseSetting("Database:MigrateOnStartup", "true");
            builder.UseSetting("Backup:Enabled", "false");
            builder.ConfigureServices(FakeUserStore.Register);
        });

    static async Task<LogRow?> PollForRowAsync(
        WebApplicationFactory<Program> factory, Func<LogFilter, LogFilter> withFilter, Func<LogRow, bool> matches,
        CancellationToken cancellationToken, int attempts = 20)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            using var scope = factory.Services.CreateScope();
            var query = scope.ServiceProvider.GetRequiredService<ILogQuery>();
            var page = await query.QueryAsync(withFilter(new LogFilter()), 0, 100, cancellationToken);
            var row = page.Rows.FirstOrDefault(matches);
            if (row is not null)
                return row;

            if (attempt < attempts - 1)
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        return null;
    }

    static string ConnectionStringFor(string databaseName) =>
        new NpgsqlConnectionStringBuilder(DatabaseSettings.AdminConnectionString) { Database = databaseName }.ConnectionString;

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
