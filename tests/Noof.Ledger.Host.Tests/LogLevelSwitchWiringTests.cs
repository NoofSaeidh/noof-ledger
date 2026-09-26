using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Noof.Ledger.Host.Logging;
using Serilog.Events;

namespace Noof.Ledger.Host.Tests;

public class LogLevelSwitchWiringTests
{
    const string UnreachableConnectionString = "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2";

    [Fact]
    public async Task Setting_the_database_switch_changes_what_an_existing_ILogger_reports_as_enabled()
    {
        string? logDirectory = null;
        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                logDirectory = builder.UseTempLogDirectory();
                builder.UseSetting("Database:MigrateOnStartup", "false");
                builder.UseSetting("Backup:Enabled", "false");
                builder.UseSetting("ConnectionStrings:Ledger", UnreachableConnectionString);
                builder.UseSetting("Serilog:MinimumLevel:Default", "Information");
            });

            using var client = factory.CreateClient();
            await client.GetAsync("/healthz", TestContext.Current.CancellationToken);

            var logger = factory.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Noof.Ledger.Host.Tests.WiringProbe");
            var switches = factory.Services.GetRequiredService<LogLevelSwitches>();

            logger.IsEnabled(LogLevel.Debug).Should().BeFalse();

            switches.SetDatabaseLevel(LogEventLevel.Debug);
            logger.IsEnabled(LogLevel.Debug).Should().BeTrue();

            switches.SetDatabaseLevel(LogEventLevel.Information);
            logger.IsEnabled(LogLevel.Debug).Should().BeFalse();
        }
        finally
        {
            if (logDirectory is not null)
                await DeleteWithRetryAsync(new DirectoryInfo(logDirectory));
        }
    }

    [Fact]
    public async Task LogLevelSwitches_resolved_from_two_scopes_is_the_same_instance()
    {
        string? logDirectory = null;
        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                logDirectory = builder.UseTempLogDirectory();
                builder.UseSetting("Database:MigrateOnStartup", "false");
                builder.UseSetting("Backup:Enabled", "false");
                builder.UseSetting("ConnectionStrings:Ledger", UnreachableConnectionString);
            });

            using var client = factory.CreateClient();
            await client.GetAsync("/healthz", TestContext.Current.CancellationToken);

            using var scopeA = factory.Services.CreateScope();
            using var scopeB = factory.Services.CreateScope();

            var switchesA = scopeA.ServiceProvider.GetRequiredService<LogLevelSwitches>();
            var switchesB = scopeB.ServiceProvider.GetRequiredService<LogLevelSwitches>();

            switchesA.Should().BeSameAs(switchesB);
        }
        finally
        {
            if (logDirectory is not null)
                await DeleteWithRetryAsync(new DirectoryInfo(logDirectory));
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
