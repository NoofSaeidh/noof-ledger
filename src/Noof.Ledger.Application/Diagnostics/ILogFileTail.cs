namespace Noof.Ledger.Application.Diagnostics;

public interface ILogFileTail
{
    Task<IReadOnlyList<string>> ReadLastLinesAsync(int count, CancellationToken cancellationToken);
}
