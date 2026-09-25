using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Noof.Ledger.Host.Tests;

public class LogLevelConfigurationTests
{
    const string UnreachableConnectionString = "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2";

    [Fact]
    public async Task An_override_for_the_apps_own_namespace_suppresses_an_Information_event_below_it()
    {
        string? logDirectory = null;

        try
        {
            var suppressedMarker = $"suppressed-{Guid.NewGuid():N}";
            var passingMarker = $"passing-{Guid.NewGuid():N}";

            using (var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                logDirectory = builder.UseTempLogDirectory();
                builder.UseSetting("Database:MigrateOnStartup", "false");
                builder.UseSetting("Backup:Enabled", "false");
                builder.UseSetting("ConnectionStrings:Ledger", UnreachableConnectionString);
                builder.UseSetting("Serilog:MinimumLevel:Override:Noof.Ledger.Host.Tests", "Warning");
            }))
            {
                using var client = factory.CreateClient();
                await client.GetAsync("/healthz", TestContext.Current.CancellationToken);

                var logger = factory.Services.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Noof.Ledger.Host.Tests.Probe");
                logger.LogInformation("{Marker}", suppressedMarker);
                logger.LogWarning("{Marker}", passingMarker);
            }

            var text = await ReadAllTextWithRetryAsync(NewestLogFile(logDirectory!));
            text.Should().NotContain(suppressedMarker, "the configured Override lowers this namespace's floor to Warning");
            text.Should().Contain(passingMarker, "a Warning event still clears the overridden floor");
        }
        finally
        {
            if (logDirectory is not null)
                await DeleteWithRetryAsync(new DirectoryInfo(logDirectory));
        }
    }

    [Fact]
    public async Task EF_Database_Command_events_do_not_reach_the_file_at_the_default_override()
    {
        string? logDirectory = null;

        try
        {
            var marker = $"ef-command-{Guid.NewGuid():N}";

            using (var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                logDirectory = builder.UseTempLogDirectory();
                builder.UseSetting("Database:MigrateOnStartup", "false");
                builder.UseSetting("Backup:Enabled", "false");
                builder.UseSetting("ConnectionStrings:Ledger", UnreachableConnectionString);
            }))
            {
                using var client = factory.CreateClient();
                await client.GetAsync("/healthz", TestContext.Current.CancellationToken);

                var logger = factory.Services.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Microsoft.EntityFrameworkCore.Database.Command");
                logger.LogDebug("{Marker}", marker);
            }

            var text = await ReadAllTextWithRetryAsync(NewestLogFile(logDirectory!));
            text.Should().NotContain(marker, "the default Override for Microsoft.EntityFrameworkCore is Information");
        }
        finally
        {
            if (logDirectory is not null)
                await DeleteWithRetryAsync(new DirectoryInfo(logDirectory));
        }
    }

    [Fact]
    public async Task The_console_sink_does_not_receive_a_Debug_event_the_file_sink_does()
    {
        string? logDirectory = null;
        var originalOut = Console.Out;
        var capturedConsole = new StringWriter();

        try
        {
            var marker = $"debug-only-{Guid.NewGuid():N}";

            using (var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                logDirectory = builder.UseTempLogDirectory();
                builder.UseSetting("Database:MigrateOnStartup", "false");
                builder.UseSetting("Backup:Enabled", "false");
                builder.UseSetting("ConnectionStrings:Ledger", UnreachableConnectionString);
            }))
            {
                using var client = factory.CreateClient();
                await client.GetAsync("/healthz", TestContext.Current.CancellationToken);

                var logger = factory.Services.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Noof.Ledger.Host.Tests.ConsoleProbe");

                Console.SetOut(capturedConsole);
                try
                {
                    logger.LogDebug("{Marker}", marker);
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }

            capturedConsole.ToString().Should().NotContain(marker, "the console sink is restricted to Information");

            var fileText = await ReadAllTextWithRetryAsync(NewestLogFile(logDirectory!));
            fileText.Should().Contain(marker, "the file sink receives everything that passes the configured minimum");
        }
        finally
        {
            Console.SetOut(originalOut);
            if (logDirectory is not null)
                await DeleteWithRetryAsync(new DirectoryInfo(logDirectory));
        }
    }

    static string NewestLogFile(string logDirectory) =>
        Directory.EnumerateFiles(logDirectory, "noof-ledger-*.log").Single();

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
