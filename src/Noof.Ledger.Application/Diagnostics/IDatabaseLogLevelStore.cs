namespace Noof.Ledger.Application.Diagnostics;

public interface IDatabaseLogLevelStore
{
    Task<LogSeverity?> GetAsync(CancellationToken cancellationToken);
    Task SaveAsync(LogSeverity level, CancellationToken cancellationToken);
}
