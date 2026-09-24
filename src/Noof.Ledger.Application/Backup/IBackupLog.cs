namespace Noof.Ledger.Application.Backup;

public interface IBackupLog
{
    Task RecordAsync(BackupRunRecord run, CancellationToken cancellationToken);
    Task<BackupStatus> StatusAsync(CancellationToken cancellationToken);
}
