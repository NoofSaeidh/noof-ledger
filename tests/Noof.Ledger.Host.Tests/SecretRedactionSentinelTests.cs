using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Host.Diagnostics;
using Noof.Ledger.TestKit;
using Npgsql;

namespace Noof.Ledger.Host.Tests;

[Collection("app-log-sink")]
public sealed class SecretRedactionSentinelTests
{
    const string FakeSecretValue = "sk-sentinel-fake-secret-0123456789";

    [Fact]
    public async Task A_registered_secret_never_appears_in_the_log_file_or_app_log_even_inside_an_exception_message()
    {
        if (!await DatabaseIsReachableAsync(TestContext.Current.CancellationToken))
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var databaseName = $"noof_secret_sentinel_{Guid.NewGuid():N}";
        await CreateCloneAsync(databaseName, TestContext.Current.CancellationToken);
        var connectionString = new NpgsqlConnectionStringBuilder(DatabaseSettings.AdminConnectionString) { Database = databaseName }.ConnectionString;
        string? logDirectory = null;

        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                logDirectory = builder.UseTempLogDirectory();
                builder.UseSetting("ConnectionStrings:Ledger", connectionString);
                builder.UseSetting("Database:MigrateOnStartup", "true");
                builder.UseSetting("Backup:Enabled", "false");
                builder.ConfigureServices(FakeUserStore.Register);
            });

            using var client = factory.CreateClient();

            var gate = factory.Services.GetRequiredService<IDatabaseGate>();
            await gate.WaitUntilReadyAsync(TestContext.Current.CancellationToken);

            using (var scope = factory.Services.CreateScope())
            {
                var secretStore = scope.ServiceProvider.GetRequiredService<ISecretStore>();
                await secretStore.SetAsync(SecretKeys.TelegramBotToken, FakeSecretValue, TestContext.Current.CancellationToken);
            }

            var snapshot = factory.Services.GetRequiredService<SecretSnapshot>();
            await snapshot.RefreshAsync(TestContext.Current.CancellationToken);

            var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Noof.Ledger.Host.Tests.Sentinel");
            var probeId = Guid.NewGuid();
            try
            {
                throw new InvalidOperationException($"call failed for token {FakeSecretValue} (probe {probeId})");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Sentinel probe {ProbeId} failed with a secret in the message", probeId);
            }

            var fileText = await PollFileTextAsync(logDirectory!, probeId.ToString(), TestContext.Current.CancellationToken);
            fileText.Should().Contain(probeId.ToString());
            fileText.Should().NotContain(FakeSecretValue);
            fileText.Should().Contain("***");

            var dbText = await PollAppLogTextAsync(connectionString, probeId.ToString(), TestContext.Current.CancellationToken);
            dbText.Should().NotBeNull();
            dbText.Should().NotContain(FakeSecretValue);
            dbText.Should().Contain("***");
        }
        finally
        {
            await DropCloneAsync(databaseName);
            if (logDirectory is not null && Directory.Exists(logDirectory))
                Directory.Delete(logDirectory, recursive: true);
        }
    }

    static async Task<string> PollFileTextAsync(string directory, string mustContain, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (Directory.Exists(directory))
            {
                var newest = Directory.EnumerateFiles(directory, "noof-ledger-*.log")
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (newest is not null)
                {
                    // The host is still running and Serilog's rolling file sink holds the file open
                    // for writing while this polls it, so an exclusive-open attempt can race a flush
                    // in flight - read with FileShare.ReadWrite rather than File.ReadAllTextAsync's
                    // default share mode.
                    string? text = null;
                    try
                    {
                        await using var stream = new FileStream(newest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var reader = new StreamReader(stream);
                        text = await reader.ReadToEndAsync(cancellationToken);
                    }
                    catch (IOException)
                    {
                    }

                    if (text is not null && text.Contains(mustContain, StringComparison.Ordinal))
                        return text;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }

        throw new TimeoutException("The sentinel line never appeared in the log file.");
    }

    static async Task<string?> PollAppLogTextAsync(string connectionString, string mustContain, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                "SELECT message || ' ' || COALESCE(exception, '') FROM app_log WHERE message LIKE @pattern OR exception LIKE @pattern",
                connection);
            command.Parameters.AddWithValue("pattern", $"%{mustContain}%");
            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is string text)
                return text;

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
