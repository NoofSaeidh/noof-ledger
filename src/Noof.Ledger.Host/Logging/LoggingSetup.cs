using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Host.Diagnostics;
using Noof.Ledger.Persistence;
using NpgsqlTypes;
using Serilog;
using Serilog.Debugging;
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

    const string DefaultDirectory = @"%LOCALAPPDATA%\NoofLedger\logs";
    const long FileSizeLimitBytes = 50 * 1024 * 1024;
    const int RetainedFileCountLimit = 14;

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

    public static Serilog.ILogger CreateBootstrapLogger(string logDirectory) =>
        new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(logDirectory, "noof-ledger-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedFileCountLimit,
                fileSizeLimitBytes: FileSizeLimitBytes,
                rollOnFileSizeLimit: true)
            .CreateBootstrapLogger();

    // The full reconfiguration UseSerilog runs once the host is built. `services` is threaded
    // through so Task 4's Postgres sink and secret-redaction wrap can resolve IDatabaseGate,
    // ILogSinkStatus and SecretRedactor here. MinimumLevel and LogContext enrichment stay on the
    // outer `configuration` - the one Log.Logger actually becomes - so a later BeginScope (Task 3's
    // TransactionLogScope) is still ambient when an event is written. `destinations` is a separate
    // inner LoggerConfiguration holding console+file+Postgres; RedactingSink wraps its built logger
    // so every one of those three sinks only ever sees a redacted LogEvent, per spec.
    public static void Configure(LoggerConfiguration configuration, string logDirectory, IServiceProvider services)
    {
        var gate = services.GetRequiredService<IDatabaseGate>();
        var sinkStatus = services.GetRequiredService<ILogSinkStatus>();
        var timeProvider = services.GetRequiredService<TimeProvider>();
        var redactor = services.GetRequiredService<SecretRedactor>();
        var connectionString = LedgerConnectionString.Resolve(
            services.GetRequiredService<IConfiguration>().GetConnectionString("Ledger"));

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

        var destinations = new LoggerConfiguration()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(logDirectory, "noof-ledger-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedFileCountLimit,
                fileSizeLimitBytes: FileSizeLimitBytes,
                rollOnFileSizeLimit: true)
            .WriteTo.Logger(pg => pg
                .Filter.ByIncludingOnly(_ => gate.State == DatabaseState.Ready)
                .WriteTo.PostgreSQL(connectionString, "app_log", columnOptions, needAutoCreateTable: false));

        var builtDestinations = destinations.CreateLogger();

        configuration
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.Sink(new RedactingSink(builtDestinations, redactor));
    }
}
