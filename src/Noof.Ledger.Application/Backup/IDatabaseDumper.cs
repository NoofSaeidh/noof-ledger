namespace Noof.Ledger.Application.Backup;

public interface IDatabaseDumper
{
    Task<DumpResult> DumpAsync(string targetPath, CancellationToken cancellationToken);
}
