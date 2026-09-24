using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Noof.Ledger.Host.Tests;

public class LoggingBootstrapTests
{
    [Fact]
    public async Task Starting_the_host_writes_a_line_into_the_configured_log_directory()
    {
        var logDirectory = Directory.CreateTempSubdirectory("noof-ledger-logs-test-");
        var previousEnvironmentValue = Environment.GetEnvironmentVariable("Logging__File__Directory");

        try
        {
            Environment.SetEnvironmentVariable("Logging__File__Directory", logDirectory.FullName);

            using (var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Database:MigrateOnStartup", "false");
                builder.UseSetting("Backup:Enabled", "false");
                builder.UseSetting("ConnectionStrings:Ledger",
                    "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
                builder.UseSetting("Logging:File:Directory", logDirectory.FullName);
            }))
            {
                using var client = factory.CreateClient();
                var response = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);
                response.IsSuccessStatusCode.Should().BeTrue();
            }

            // The factory (and the host it captured) is disposed above, before the file is read:
            // Serilog's file sink is only guaranteed closed once the host that owns it stops, and
            // reading while it is still held open races an exclusive lock on Windows.
            var logFiles = Directory.EnumerateFiles(logDirectory.FullName, "noof-ledger-*.log").ToArray();
            logFiles.Should().ContainSingle("the bootstrap logger must write into the directory the configuration names");
            (await ReadAllTextWithRetryAsync(logFiles[0])).Should().NotBeNullOrWhiteSpace(
                "starting the host must produce at least one log line in the configured file");
        }
        finally
        {
            Environment.SetEnvironmentVariable("Logging__File__Directory", previousEnvironmentValue);
            await DeleteWithRetryAsync(logDirectory);
        }
    }

    // Serilog's rolling file sink releases its handle asynchronously even after the host that owns
    // it is disposed, so an immediate read or delete can race a close still in flight - not a bug
    // in the logging pipeline itself, just cleanup outliving disposal by a beat.
    static async Task<string> ReadAllTextWithRetryAsync(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException) when (attempt < 10)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }
    }

    static async Task DeleteWithRetryAsync(DirectoryInfo directory)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                directory.Delete(recursive: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }
    }
}
