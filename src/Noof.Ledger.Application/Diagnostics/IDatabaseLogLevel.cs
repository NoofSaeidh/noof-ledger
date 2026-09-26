namespace Noof.Ledger.Application.Diagnostics;

public interface IDatabaseLogLevel
{
    // null means Off: nothing is written to app_log.
    LogSeverity? Current { get; }
    IReadOnlyList<LogSeverity> Choices { get; }
    Task SetAsync(LogSeverity? level, CancellationToken cancellationToken);
}
