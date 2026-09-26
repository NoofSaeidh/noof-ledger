namespace Noof.Ledger.Application.Diagnostics;

public interface IDatabaseLogLevelStore
{
    Task<DatabaseLogLevelSetting?> GetAsync(CancellationToken cancellationToken);
    Task SaveAsync(DatabaseLogLevelSetting setting, CancellationToken cancellationToken);
}
