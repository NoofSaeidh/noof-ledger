using Noof.Ledger.Application.Diagnostics;
using Npgsql;

namespace Noof.Ledger.Host.Startup;

internal enum DatabaseStartupResult { Ready, WaitingRetry, FailedRetry }

internal sealed partial class DatabaseStartupService(
    IDatabaseStartupProbe probe,
    DatabaseGate gate,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<DatabaseStartupService> logger)
    : BackgroundService
{
    // 2, 4, 8, 16, 30, 30, 30 ... - the last step repeats forever (O-1: the host waits
    // indefinitely). A connection-level failure never reaches Failed - the database simply is not
    // up yet, and that is not an error worth escalating past a Warning.
    static readonly TimeSpan[] ConnectionBackoffSteps =
    [
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30),
    ];

    static readonly TimeSpan FailedRetryInterval = TimeSpan.FromMinutes(5);

    int connectionAttempt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var result = await RunAttemptAsync(stoppingToken);
            if (result == DatabaseStartupResult.Ready)
                return;

            await DelayQuietlyAsync(NextDelay(), stoppingToken);
        }
    }

    // Public so the fast test suite can drive one classification/gate-transition at a time without
    // going through BackgroundService.StartAsync (CategorizationWorker's RunTickAsync is the same
    // shape, for the same reason).
    public async Task<DatabaseStartupResult> RunAttemptAsync(CancellationToken cancellationToken)
    {
        try
        {
            await probe.OpenConnectionAsync(cancellationToken);
            connectionAttempt = 0;

            if (configuration.GetValue("Database:MigrateOnStartup", true))
            {
                gate.Set(DatabaseState.Migrating, null);
                await probe.MigrateAsync(cancellationToken);
            }

            gate.Set(DatabaseState.Ready, null);
            return DatabaseStartupResult.Ready;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && IsConnectionFailure(ex))
        {
            connectionAttempt++;
            gate.Set(DatabaseState.Waiting, ex.Message);

            if (connectionAttempt <= 10 || connectionAttempt % 10 == 0)
                LogConnectionAttemptFailed(logger, connectionAttempt, ex);

            return DatabaseStartupResult.WaitingRetry;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // PostgresException.Message prefixes the raw text with its SqlState ("42P07: ..."); the
            // gate's Detail is meant for a human reading the log or the sign-in banner, so it uses
            // MessageText, the same string without that prefix.
            gate.Set(DatabaseState.Failed, ex is PostgresException postgres ? postgres.MessageText : ex.Message);
            LogMigrationFailed(logger, ex);

            return DatabaseStartupResult.FailedRetry;
        }
    }

    // The delay ExecuteAsync waits before its next attempt, given the outcome RunAttemptAsync just
    // recorded. Public so the backoff sequence itself is a fast, non-timing assertion.
    public TimeSpan NextDelay() => gate.State switch
    {
        DatabaseState.Failed => FailedRetryInterval,
        _ => ConnectionBackoffSteps[Math.Min(Math.Max(connectionAttempt, 1) - 1, ConnectionBackoffSteps.Length - 1)],
    };

    async Task DelayQuietlyAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, timeProvider, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Npgsql wraps a refused/timed-out socket in an NpgsqlException whose SqlState is null - a real
    // PostgresException (the server answered and rejected something) always carries one. A bare
    // SocketException or TimeoutException can also surface unwrapped, depending on where in the
    // connect sequence Npgsql gives up.
    //
    // I-4 (Phase 5 final review): not every PostgresException means "the migration is wrong" -
    // classes 57 (operator intervention: starting up, shutting down, admin-killed) and 08
    // (connection exception) and 53300 (too many connections) are the server answering while it is
    // not yet - or no longer - ready to serve, exactly the transient state this service exists to
    // wait out. Only an error the server raises once it IS up and serving (a real migration
    // conflict, a bad SQL statement) belongs in Failed.
    static bool IsConnectionFailure(Exception ex) => ex switch
    {
        PostgresException postgres => IsConnectionLevelSqlState(postgres.SqlState),
        NpgsqlException npgsqlException => npgsqlException.SqlState is null,
        System.Net.Sockets.SocketException => true,
        TimeoutException => true,
        _ => ex.InnerException is { } inner && IsConnectionFailure(inner),
    };

    static bool IsConnectionLevelSqlState(string? sqlState) =>
        sqlState is not null && (sqlState.StartsWith("57", StringComparison.Ordinal)
            || sqlState.StartsWith("08", StringComparison.Ordinal)
            || sqlState == "53300");

    [LoggerMessage(EventId = 5101, Level = LogLevel.Warning,
        Message = "Database connection attempt {Attempt} failed; retrying")]
    static partial void LogConnectionAttemptFailed(ILogger logger, int attempt, Exception exception);

    [LoggerMessage(EventId = 5102, Level = LogLevel.Error,
        Message = "Database migration failed; retrying in 5 minutes")]
    static partial void LogMigrationFailed(ILogger logger, Exception exception);
}
