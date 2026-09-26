namespace Noof.Ledger.Application.Diagnostics;

public interface ILogRetentionSettings
{
    // Always returns a usable value: LogRetentionDays.Default when nothing has been saved yet.
    Task<LogRetentionDays> GetAsync(CancellationToken cancellationToken);
    Task SaveAsync(LogRetentionDays days, CancellationToken cancellationToken);
}
