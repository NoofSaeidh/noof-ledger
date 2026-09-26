namespace Noof.Ledger.Application.Diagnostics;

public interface ILogQuery
{
    Task<LogPage> QueryAsync(LogFilter filter, int pageIndex, int pageSize, CancellationToken cancellationToken);
}
