namespace Noof.Ledger.Application.Diagnostics;

public interface ILogRetention
{
    Task<int> PruneAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
