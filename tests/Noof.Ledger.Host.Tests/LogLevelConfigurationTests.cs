using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Noof.Ledger.Host.Logging;
using Serilog;
using Serilog.Events;

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

    // I-1 (Phase 5 final review): these two call LoggingSetup.Configure directly, not through a
    // WebApplicationFactory host - Program.cs's top-level catch swallows a startup exception into
    // Environment.ExitCode, which a WebApplicationFactory can only observe as "the entry point
    // exited without ever building an IHost", losing the actual message these tests need to assert.
    [Fact]
    public void A_configured_WriteTo_sink_is_never_created_while_MinimumLevel_settings_still_apply()
    {
        var logDirectory = Directory.CreateTempSubdirectory("noof-logging-setup-test-").FullName;
        var injectedSinkPath = Path.Combine(Path.GetTempPath(), $"noof-injected-sink-{Guid.NewGuid():N}.log");

        try
        {
            var hostConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Serilog:WriteTo:0:Name"] = "File",
                    ["Serilog:WriteTo:0:Args:path"] = injectedSinkPath,
                    ["Serilog:MinimumLevel:Override:Noof.Ledger.Host.Tests"] = "Warning",
                })
                .Build();

            var suppressedMarker = $"suppressed-{Guid.NewGuid():N}";
            var passingMarker = $"passing-{Guid.NewGuid():N}";

            var configuration = new LoggerConfiguration();
            LoggingSetup.Configure(configuration, hostConfiguration, logDirectory, UnreachableConnectionString, BuildFakeServices());
            using (var logger = configuration.CreateLogger())
            {
                var probe = logger.ForContext(Serilog.Core.Constants.SourceContextPropertyName, "Noof.Ledger.Host.Tests.Probe");
                probe.Information("{Marker}", suppressedMarker);
                probe.Warning("{Marker}", passingMarker);
            }

            File.Exists(injectedSinkPath).Should().BeFalse(
                "Serilog:WriteTo entries in configuration must never add a sink - only MinimumLevel is read from configuration");

            var text = ReadAllTextWithRetry(NewestLogFile(logDirectory));
            text.Should().NotContain(suppressedMarker, "the configured Override still applies without ReadFrom.Configuration");
            text.Should().Contain(passingMarker, "a Warning event still clears the overridden floor");
        }
        finally
        {
            DeleteWithRetry(new DirectoryInfo(logDirectory));
            if (File.Exists(injectedSinkPath))
                File.Delete(injectedSinkPath);
        }
    }

    [Fact]
    public void An_invalid_MinimumLevel_value_fails_with_a_clear_error()
    {
        var logDirectory = Directory.CreateTempSubdirectory("noof-logging-setup-test-").FullName;
        try
        {
            var hostConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Serilog:MinimumLevel:Default"] = "NotALevel",
                })
                .Build();

            var act = () => LoggingSetup.Configure(
                new LoggerConfiguration(), hostConfiguration, logDirectory, UnreachableConnectionString, BuildFakeServices());

            act.Should().Throw<InvalidOperationException>().WithMessage("*Serilog:MinimumLevel:Default*NotALevel*");
        }
        finally
        {
            DeleteWithRetry(new DirectoryInfo(logDirectory));
        }
    }

    // M-9 (Phase 5 final review): end-to-end through LoggingSetup's real wiring (not just
    // ReadyGatedBufferSinkTests' own unit tests) - a gate that never turns Ready means the events
    // buffered before shutdown never reach app_log; disposing the outer logger must still leave a
    // trace of that in the file, since there is no database connection left to write to.
    [Fact]
    public void Disposing_the_logger_while_the_gate_never_became_Ready_leaves_a_warning_in_the_file()
    {
        var logDirectory = Directory.CreateTempSubdirectory("noof-logging-setup-test-").FullName;
        try
        {
            var hostConfiguration = new ConfigurationBuilder().Build();
            var configuration = new LoggerConfiguration();
            LoggingSetup.Configure(
                configuration, hostConfiguration, logDirectory, UnreachableConnectionString, BuildFakeServices(new NeverReadyGate()));

            var logger = configuration.CreateLogger();
            var probe = logger.ForContext(Serilog.Core.Constants.SourceContextPropertyName, "Noof.Ledger.Host.Tests.Probe");
            probe.Warning("first buffered event");
            probe.Warning("second buffered event");
            logger.Dispose();

            var text = ReadAllTextWithRetry(NewestLogFile(logDirectory));
            text.Should().Contain("2", "the file must name how many buffered events never reached the database");
            text.Should().Contain("never written to the database");
        }
        finally
        {
            DeleteWithRetry(new DirectoryInfo(logDirectory));
        }
    }

    [Fact]
    public void The_database_switch_decides_which_levels_reach_the_database_buffer()
    {
        var logDirectory = Directory.CreateTempSubdirectory("noof-logging-setup-test-").FullName;
        try
        {
            var hostConfiguration = new ConfigurationBuilder().Build();
            var fakeServices = BuildFakeServices(new NeverReadyGate());
            var switches = fakeServices.GetRequiredService<LogLevelSwitches>();
            switches.SetDatabaseLevel(LogEventLevel.Debug);

            var configuration = new LoggerConfiguration();
            LoggingSetup.Configure(configuration, hostConfiguration, logDirectory, UnreachableConnectionString, fakeServices);
            using (var logger = configuration.CreateLogger())
            {
                var probe = logger.ForContext(Serilog.Core.Constants.SourceContextPropertyName, "Noof.Ledger.Host.Tests.Probe");
                probe.Debug("debug marker");
                probe.Information("information marker");
            }

            var text = ReadAllTextWithRetry(NewestLogFile(logDirectory));
            text.Should().Contain("2", "both events cleared the Debug database floor and were buffered");
            text.Should().Contain("never written to the database");
        }
        finally
        {
            DeleteWithRetry(new DirectoryInfo(logDirectory));
        }
    }

    [Fact]
    public void The_database_switch_at_Information_only_buffers_the_Information_event()
    {
        var logDirectory = Directory.CreateTempSubdirectory("noof-logging-setup-test-").FullName;
        try
        {
            var hostConfiguration = new ConfigurationBuilder().Build();
            var fakeServices = BuildFakeServices(new NeverReadyGate());
            var switches = fakeServices.GetRequiredService<LogLevelSwitches>();
            switches.SetDatabaseLevel(LogEventLevel.Information);

            var configuration = new LoggerConfiguration();
            LoggingSetup.Configure(configuration, hostConfiguration, logDirectory, UnreachableConnectionString, fakeServices);
            using (var logger = configuration.CreateLogger())
            {
                var probe = logger.ForContext(Serilog.Core.Constants.SourceContextPropertyName, "Noof.Ledger.Host.Tests.Probe");
                probe.Debug("debug marker");
                probe.Information("information marker");
            }

            var text = ReadAllTextWithRetry(NewestLogFile(logDirectory));
            text.Should().Contain("1", "only the Information event cleared the database floor");
            text.Should().Contain("never written to the database");
        }
        finally
        {
            DeleteWithRetry(new DirectoryInfo(logDirectory));
        }
    }

    [Fact]
    public void Default_is_the_files_floor_and_Information_stops_a_Debug_marker_reaching_the_file()
    {
        var logDirectory = Directory.CreateTempSubdirectory("noof-logging-setup-test-").FullName;
        try
        {
            var marker = $"debug-{Guid.NewGuid():N}";
            var hostConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Serilog:MinimumLevel:Default"] = "Information" })
                .Build();
            var fakeServices = BuildFakeServices();
            fakeServices.GetRequiredService<LogLevelSwitches>().SetDatabaseLevel(LogEventLevel.Debug);

            var configuration = new LoggerConfiguration();
            LoggingSetup.Configure(configuration, hostConfiguration, logDirectory, UnreachableConnectionString, fakeServices);
            using (var logger = configuration.CreateLogger())
            {
                var probe = logger.ForContext(Serilog.Core.Constants.SourceContextPropertyName, "Noof.Ledger.Host.Tests.Probe");
                probe.Debug("{Marker}", marker);
                probe.Information("file marker, to guarantee the file exists");
            }

            var text = ReadAllTextWithRetry(NewestLogFile(logDirectory));
            text.Should().NotContain(marker, "the file's floor is Default, not the database level");
        }
        finally
        {
            DeleteWithRetry(new DirectoryInfo(logDirectory));
        }
    }

    [Fact]
    public void Default_at_Debug_lets_a_Debug_marker_reach_the_file()
    {
        var logDirectory = Directory.CreateTempSubdirectory("noof-logging-setup-test-").FullName;
        try
        {
            var marker = $"debug-{Guid.NewGuid():N}";
            var hostConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Serilog:MinimumLevel:Default"] = "Debug" })
                .Build();

            var configuration = new LoggerConfiguration();
            LoggingSetup.Configure(configuration, hostConfiguration, logDirectory, UnreachableConnectionString, BuildFakeServices());
            using (var logger = configuration.CreateLogger())
            {
                var probe = logger.ForContext(Serilog.Core.Constants.SourceContextPropertyName, "Noof.Ledger.Host.Tests.Probe");
                probe.Debug("{Marker}", marker);
            }

            var text = ReadAllTextWithRetry(NewestLogFile(logDirectory));
            text.Should().Contain(marker, "Default=Debug is the file's floor");
        }
        finally
        {
            DeleteWithRetry(new DirectoryInfo(logDirectory));
        }
    }

    static IServiceProvider BuildFakeServices(Noof.Ledger.Application.Diagnostics.IDatabaseGate? gate = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(gate ?? new AlwaysReadyGate());
        services.AddSingleton<Noof.Ledger.Application.Diagnostics.ILogSinkStatus>(new NoopSinkStatus());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<Noof.Ledger.Host.Diagnostics.ISecretValueSource>(new NoSecrets());
        services.AddSingleton<Noof.Ledger.Host.Diagnostics.SecretRedactor>();
        services.AddSingleton<LogLevelSwitches>();
        return services.BuildServiceProvider();
    }

    sealed class AlwaysReadyGate : Noof.Ledger.Application.Diagnostics.IDatabaseGate
    {
        public Noof.Ledger.Application.Diagnostics.DatabaseState State => Noof.Ledger.Application.Diagnostics.DatabaseState.Ready;
        public string? Detail => null;
        public Task WaitUntilReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // M-9 (Phase 5 final review): a gate that never turns Ready, for proving ReadyGatedBufferSink's
    // dispose-time fallback warning reaches the real file sink end to end through LoggingSetup's own
    // wiring, not just the sink's own unit tests.
    sealed class NeverReadyGate : Noof.Ledger.Application.Diagnostics.IDatabaseGate
    {
        public Noof.Ledger.Application.Diagnostics.DatabaseState State => Noof.Ledger.Application.Diagnostics.DatabaseState.Waiting;
        public string? Detail => null;
        public Task WaitUntilReadyAsync(CancellationToken cancellationToken) => new TaskCompletionSource().Task;
    }

    sealed class NoopSinkStatus : Noof.Ledger.Application.Diagnostics.ILogSinkStatus
    {
        public DateTimeOffset? LastFailureAt { get; private set; }
        public void RecordFailure(DateTimeOffset at) => LastFailureAt = at;
    }

    sealed class NoSecrets : Noof.Ledger.Host.Diagnostics.ISecretValueSource
    {
        public IReadOnlyCollection<string> CurrentValues => [];
    }

    static string ReadAllTextWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(50);
            }
        }
    }

    static void DeleteWithRetry(DirectoryInfo directory)
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
                Thread.Sleep(50);
            }
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
