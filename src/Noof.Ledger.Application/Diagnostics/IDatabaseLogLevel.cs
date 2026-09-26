namespace Noof.Ledger.Application.Diagnostics;

public interface IDatabaseLogLevel
{
    LogSeverity Current { get; }
    IReadOnlyList<LogSeverity> Choices { get; }
    Task SetAsync(LogSeverity level, CancellationToken cancellationToken);
}
