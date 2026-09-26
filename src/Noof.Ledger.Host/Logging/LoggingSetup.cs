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
    const string FileMinimumLevelConfigKey = "Logging:File:MinimumLevel";
    const string ConsoleMinimumLevelConfigKey = "Logging:Console:MinimumLevel";

    // I-1's successor (decision (d), 2026-09-26): Serilog.Settings.Configuration's own
    // Serilog:MinimumLevel:Default key is deliberately never read any more - the file and console
    // sinks each carry their own explicit floor above, so a value left over from before this change
    // would silently apply to neither and quietly stop doing anything. ReadMinimumLevels below fails
    // fast the moment it sees this key rather than let that happen unnoticed.
    const string LegacyDefaultConfigKey = "Serilog:MinimumLevel:Default";

    const string DefaultDirectory = @"%LOCALAPPDATA%\NoofLedger\logs";
    const long DefaultFileSizeLimitBytes = 50 * 1024 * 1024;
    const int DefaultRetainedFileCountLimit = 14;
    const LogEventLevel DefaultFileMinimumLevel = LogEventLevel.Debug;
    const LogEventLevel DefaultConsoleMinimumLevel = LogEventLevel.Information;

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

    static LogEventLevel ResolveFileMinimumLevel(IConfiguration configuration) =>
        configuration[FileMinimumLevelConfigKey] is { } value
            ? ParseLevel(value, FileMinimumLevelConfigKey)
            : DefaultFileMinimumLevel;

    static LogEventLevel ResolveConsoleMinimumLevel(IConfiguration configuration) =>
        configuration[ConsoleMinimumLevelConfigKey] is { } value
            ? ParseLevel(value, ConsoleMinimumLevelConfigKey)
            : DefaultConsoleMinimumLevel;

    // A startup exception (a bad connection string, a locked log file) is reported through this
    // logger before UseSerilog ever runs - the console+file sinks below are wrapped in the same
    // RedactingSink the fully configured logger uses in Configure(), so it never prints the
    // database password `redactor` was seeded with in the clear.
    public static Serilog.ILogger CreateBootstrapLogger(string logDirectory, SecretRedactor redactor, IConfiguration configuration)
    {
        var fileLevel = ResolveFileMinimumLevel(configuration);
        var consoleLevel = ResolveConsoleMinimumLevel(configuration);

        var destinations = new LoggerConfiguration()
            .WriteTo.Console(restrictedToMinimumLevel: consoleLevel)
            .WriteTo.File(
                Path.Combine(logDirectory, "noof-ledger-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: ResolveRetainedFileCountLimit(configuration),
                fileSizeLimitBytes: ResolveFileSizeLimitBytes(configuration),
                rollOnFileSizeLimit: true,
                restrictedToMinimumLevel: fileLevel)
            .CreateLogger();

        return new LoggerConfiguration()
            .MinimumLevel.Is(Min(fileLevel, consoleLevel))
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
        var switches = services.GetRequiredService<LogLevelSwitches>();

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
        // No .MinimumLevel here (final review, major): this Logger is used only as a WriteTo.Sink
        // target below (ReadyGatedBufferSink calls its ILogEventSink.Emit directly), and Serilog's
        // Logger.Emit dispatches to the sink pipeline unconditionally, never checking its own
        // MinimumLevel - only ILogger.Write (Log.Information(...) and friends) applies that check.
        // A .MinimumLevel call here would be a no-op either way (SerilogInnerLoggerSinkTests proves
        // it against the resolved Serilog version); the real floor is switches.Database on the
        // outer WriteTo.Sink call.
        var postgresLogger = new LoggerConfiguration()
            .WriteTo.PostgreSQL(connectionString, "app_log", columnOptions, period: TimeSpan.FromSeconds(1), needAutoCreateTable: false)
            .CreateLogger();

        var levels = ReadMinimumLevels(hostConfiguration);
        switches.SetFileLevel(levels.File);
        switches.SetConsoleLevel(levels.Console);

        // consoleAndFileLogger is built once and referenced twice: as `destinations`' own sink, and
        // as ReadyGatedBufferSink's fallback (M-9, Phase 5 final review) - the one place it can still
        // report to when it is disposed with events that never reached the database, since there is
        // no app_log connection to write to at that point. No .MinimumLevel here either, for the same
        // reason as postgresLogger above: each restrictedToMinimumLevel call below is what actually
        // narrows what reaches Console and File, never this inner Logger's own floor.
        var consoleAndFileLogger = new LoggerConfiguration()
            .WriteTo.Console(restrictedToMinimumLevel: levels.Console)
            .WriteTo.File(
                Path.Combine(logDirectory, "noof-ledger-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: ResolveRetainedFileCountLimit(hostConfiguration),
                fileSizeLimitBytes: ResolveFileSizeLimitBytes(hostConfiguration),
                rollOnFileSizeLimit: true,
                restrictedToMinimumLevel: levels.File)
            .CreateLogger();

        // ReadyGatedBufferSink's sink is added before consoleAndFileLogger's so that, on dispose,
        // Serilog tears sinks down in the order they were added - the buffer sink's own Dispose (it
        // may still need to Emit its fallback warning) runs while consoleAndFileLogger is still live,
        // not after it has already been disposed. levelSwitch: switches.Database sits in front of the
        // buffer sink itself, so an event below the database's runtime level never reaches - and
        // never fills - the buffer's 10,000-event pre-Ready capacity.
        var destinations = new LoggerConfiguration()
            .WriteTo.Sink(new ReadyGatedBufferSink(postgresLogger, gate, consoleAndFileLogger), levelSwitch: switches.Database)
            .WriteTo.Sink(consoleAndFileLogger);

        var builtDestinations = destinations.CreateLogger();

        // Root: controlled by switches.Root, always min(file, console, database) - see
        // LogLevelSwitches. An override still quiets a namespace for every sink, unchanged; below a
        // sink's own floor an override now stops reaching that sink too (see LogLevelSwitches' doc
        // comment for why).
        configuration.MinimumLevel.ControlledBy(switches.Root);
        foreach (var over in levels.Overrides)
            configuration.MinimumLevel.Override(over.Key, over.Value);

        configuration
            .Enrich.FromLogContext()
            .WriteTo.Sink(new RedactingSink(builtDestinations, redactor));
    }

    static LogEventLevel Min(LogEventLevel a, LogEventLevel b) => a < b ? a : b;

    // I-1 (Phase 5 final review): ReadFrom.Configuration handed the whole "Serilog" section to
    // Serilog.Settings.Configuration, which honours WriteTo/AuditTo/Enrich/Filter/Destructure too -
    // not just MinimumLevel. A stray Serilog:WriteTo:* setting (an operator's leftover environment
    // variable, or one supplied deliberately) then attached a sink directly to the outer
    // LoggerConfiguration, as a sibling of WriteTo.Sink(RedactingSink) rather than a child of it, so
    // it received every LogEvent unredacted. Reading exactly the keys named below removes that whole
    // class of sink injection - only Serilog:MinimumLevel:Override:*, Logging:File:MinimumLevel and
    // Logging:Console:MinimumLevel are ever read from configuration; every sink stays hard-coded in
    // this file.
    //
    // Decision (d), 2026-09-26: Serilog:MinimumLevel:Default is withdrawn in favour of an explicit
    // floor per static sink - Logging:File:MinimumLevel and Logging:Console:MinimumLevel - because a
    // single Default could not express "the file keeps Debug detail, the console stays terse" at
    // once, which is exactly the shape the operator wanted. A leftover Default key would silently
    // stop doing anything under the new keys, so its mere presence fails fast here instead.
    static (LogEventLevel File, LogEventLevel Console, IReadOnlyDictionary<string, LogEventLevel> Overrides) ReadMinimumLevels(
        IConfiguration hostConfiguration)
    {
        if (hostConfiguration[LegacyDefaultConfigKey] is not null)
            throw new InvalidOperationException(
                $"{LegacyDefaultConfigKey} is no longer read. Set {FileMinimumLevelConfigKey} and/or " +
                $"{ConsoleMinimumLevelConfigKey} instead.");

        var fileLevel = ResolveFileMinimumLevel(hostConfiguration);
        var consoleLevel = ResolveConsoleMinimumLevel(hostConfiguration);

        var overrides = hostConfiguration.GetSection("Serilog:MinimumLevel:Override").GetChildren()
            .ToDictionary(over => over.Key, over => ParseLevel(over.Value!, $"Serilog:MinimumLevel:Override:{over.Key}"));

        return (fileLevel, consoleLevel, overrides);
    }

    static LogEventLevel ParseLevel(string value, string key) =>
        Enum.TryParse<LogEventLevel>(value, ignoreCase: true, out var level)
            ? level
            : throw new InvalidOperationException($"{key} '{value}' is not a valid Serilog log level.");
}
