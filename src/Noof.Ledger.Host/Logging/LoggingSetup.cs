using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Host.Diagnostics;
using NpgsqlTypes;
using Serilog;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Sinks.PostgreSQL;
using Serilog.Sinks.PostgreSQL.ColumnWriters;

namespace Noof.Ledger.Host.Logging;

// Task 2 (Phase 5 observability): Serilog is only the provider behind ILogger<T> (CLAUDE.md,
// "The tool loop..." section's sibling rule for AI providers applies the same way here) - this is
// the one place in the solution allowed to name Serilog types, besides Program.cs and the
// per-type [LoggerMessage] partial classes that only ever see ILogger<T>.
internal static class LoggingSetup
{
    public const string DirectoryConfigKey = "Logging:File:Directory";
    const string FileSizeLimitBytesConfigKey = "Logging:File:FileSizeLimitBytes";
    const string RetainedFileCountLimitConfigKey = "Logging:File:RetainedFileCountLimit";

    const string DefaultDirectory = @"%LOCALAPPDATA%\NoofLedger\logs";
    const long DefaultFileSizeLimitBytes = 50 * 1024 * 1024;
    const int DefaultRetainedFileCountLimit = 14;

    // A plain ConfigurationBuilder, not builder.Configuration - this runs before
    // WebApplication.CreateBuilder exists, because the bootstrap logger must be live before
    // anything else in the host can fail loudly. AppContext.BaseDirectory, not
    // Directory.GetCurrentDirectory(): the latter is the test runner's working directory under
    // dotnet test, not the folder appsettings.json is copied into.
    public static IConfiguration BuildBootstrapConfiguration(string[] args) =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .AddCommandLine(args)
            .Build();

    public static string ResolveLogDirectory(IConfiguration configuration) =>
        Environment.ExpandEnvironmentVariables(configuration[DirectoryConfigKey] ?? DefaultDirectory);

    static long ResolveFileSizeLimitBytes(IConfiguration configuration) =>
        configuration.GetValue(FileSizeLimitBytesConfigKey, DefaultFileSizeLimitBytes);

    static int ResolveRetainedFileCountLimit(IConfiguration configuration) =>
        configuration.GetValue(RetainedFileCountLimitConfigKey, DefaultRetainedFileCountLimit);

    // A startup exception (a bad connection string, a locked log file) is reported through this
    // logger before UseSerilog ever runs - the console+file sinks below are wrapped in the same
    // RedactingSink the fully configured logger uses in Configure(), so it never prints the
    // database password `redactor` was seeded with in the clear.
    public static Serilog.ILogger CreateBootstrapLogger(string logDirectory, SecretRedactor redactor, IConfiguration configuration)
    {
        var destinations = new LoggerConfiguration()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(logDirectory, "noof-ledger-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: ResolveRetainedFileCountLimit(configuration),
                fileSizeLimitBytes: ResolveFileSizeLimitBytes(configuration),
                rollOnFileSizeLimit: true)
            .CreateLogger();

        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Sink(new RedactingSink(destinations, redactor))
            .CreateBootstrapLogger();
    }

    // The full reconfiguration UseSerilog runs once the host is built. `services` is threaded
    // through so Task 4's Postgres sink and secret-redaction wrap can resolve IDatabaseGate,
    // ILogSinkStatus and SecretRedactor here. `destinations` is a separate inner LoggerConfiguration
    // holding console+file+Postgres; RedactingSink wraps its built logger so every one of those
    // three sinks only ever sees a redacted LogEvent, per spec. `connectionString` is resolved once
    // by the caller (Program.cs) and passed in - resolving it again here duplicated
    // LedgerConnectionString.Resolve's work every time the host started.
    //
    // M-9 (Phase 5 final review): a later BeginScope (Task 3's TransactionLogScope) still reaches
    // Serilog properties regardless - SerilogLoggerProvider enriches Microsoft.Extensions.Logging
    // scopes itself, whether or not Enrich.FromLogContext() is present. FromLogContext() only feeds
    // Serilog.Context.LogContext.PushProperty, which nothing in this codebase calls; kept for
    // parity with a conventional Serilog setup, not because it is load-bearing here.
    public static void Configure(
        LoggerConfiguration configuration, IConfiguration hostConfiguration, string logDirectory,
        string connectionString, IServiceProvider services)
    {
        var gate = services.GetRequiredService<IDatabaseGate>();
        var sinkStatus = services.GetRequiredService<ILogSinkStatus>();
        var timeProvider = services.GetRequiredService<TimeProvider>();
        var redactor = services.GetRequiredService<SecretRedactor>();

        SelfLog.Enable(_ => sinkStatus.RecordFailure(timeProvider.GetUtcNow()));

        var columnOptions = new Dictionary<string, ColumnWriterBase>
        {
            ["logged_at"] = new TimestampColumnWriter(NpgsqlDbType.TimestampTz),
            ["level"] = new LevelColumnWriter(renderAsText: false, NpgsqlDbType.Smallint),
            ["source"] = new SinglePropertyColumnWriter("SourceContext", PropertyWriteMethod.Raw, NpgsqlDbType.Text),
            ["message"] = new RenderedMessageColumnWriter(NpgsqlDbType.Text),
            ["template"] = new MessageTemplateColumnWriter(NpgsqlDbType.Text),
            ["exception"] = new ExceptionColumnWriter(NpgsqlDbType.Text),
            ["transaction_id"] = new SinglePropertyColumnWriter("TransactionId", PropertyWriteMethod.Raw, NpgsqlDbType.Uuid),
            ["properties"] = new PropertiesColumnWriter(NpgsqlDbType.Jsonb),
        };

        // period: 1s, not the sink's own (much longer) default - an operator reading /diagnostics/logs
        // right after something happened should see it there, not wonder why a real event is
        // missing for the length of an unconfigured batching window.
        var postgresLogger = new LoggerConfiguration()
            .WriteTo.PostgreSQL(connectionString, "app_log", columnOptions, period: TimeSpan.FromSeconds(1), needAutoCreateTable: false)
            .CreateLogger();

        // consoleAndFileLogger is built once and referenced twice: as `destinations`' own sink, and
        // as ReadyGatedBufferSink's fallback (M-9, Phase 5 final review) - the one place it can still
        // report to when it is disposed with events that never reached the database, since there is
        // no app_log connection to write to at that point.
        var consoleAndFileLogger = new LoggerConfiguration()
            .WriteTo.Console(restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information)
            .WriteTo.File(
                Path.Combine(logDirectory, "noof-ledger-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: ResolveRetainedFileCountLimit(hostConfiguration),
                fileSizeLimitBytes: ResolveFileSizeLimitBytes(hostConfiguration),
                rollOnFileSizeLimit: true)
            .CreateLogger();

        // ReadyGatedBufferSink's sink is added before consoleAndFileLogger's so that, on dispose,
        // Serilog tears sinks down in the order they were added - the buffer sink's own Dispose (it
        // may still need to Emit its fallback warning) runs while consoleAndFileLogger is still live,
        // not after it has already been disposed.
        var destinations = new LoggerConfiguration()
            .WriteTo.Sink(new ReadyGatedBufferSink(postgresLogger, gate, consoleAndFileLogger))
            .WriteTo.Sink(consoleAndFileLogger);

        var builtDestinations = destinations.CreateLogger();

        ApplyMinimumLevel(configuration, hostConfiguration);

        configuration
            .Enrich.FromLogContext()
            .WriteTo.Sink(new RedactingSink(builtDestinations, redactor));
    }

    // I-1 (Phase 5 final review): ReadFrom.Configuration handed the whole "Serilog" section to
    // Serilog.Settings.Configuration, which honours WriteTo/AuditTo/Enrich/Filter/Destructure too -
    // not just MinimumLevel. A stray Serilog:WriteTo:* setting (an operator's leftover environment
    // variable, or one supplied deliberately) then attached a sink directly to the outer
    // LoggerConfiguration, as a sibling of WriteTo.Sink(RedactingSink) rather than a child of it, so
    // it received every LogEvent unredacted. Reading exactly the two keys the brief names removes
    // that whole class of sink injection - only MinimumLevel:Default and MinimumLevel:Override:* are
    // ever read from configuration; every sink stays hard-coded in this file.
    static void ApplyMinimumLevel(LoggerConfiguration configuration, IConfiguration hostConfiguration)
    {
        var levels = hostConfiguration.GetSection("Serilog:MinimumLevel");

        var defaultLevel = levels["Default"] is { } defaultValue
            ? ParseLevel(defaultValue, "Serilog:MinimumLevel:Default")
            : LogEventLevel.Information;
        configuration.MinimumLevel.Is(defaultLevel);

        foreach (var over in levels.GetSection("Override").GetChildren())
            configuration.MinimumLevel.Override(over.Key, ParseLevel(over.Value!, $"Serilog:MinimumLevel:Override:{over.Key}"));
    }

    static LogEventLevel ParseLevel(string value, string key) =>
        Enum.TryParse<LogEventLevel>(value, ignoreCase: true, out var level)
            ? level
            : throw new InvalidOperationException($"{key} '{value}' is not a valid Serilog log level.");
}
