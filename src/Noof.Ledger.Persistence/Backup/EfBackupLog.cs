using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Backup;

namespace Noof.Ledger.Persistence.Backup;

internal sealed class EfBackupLog(LedgerDbContext db) : IBackupLog
{
    public async Task RecordAsync(BackupRunRecord run, CancellationToken cancellationToken)
    {
        db.BackupRuns.Add(new BackupRun
        {
            Id = Guid.NewGuid(),
            StartedAt = run.StartedAt,
            FinishedAt = run.FinishedAt,
            Succeeded = run.Succeeded,
            FileName = run.FileName,
            SizeBytes = run.SizeBytes,
            Error = run.Error,
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<BackupStatus> StatusAsync(CancellationToken cancellationToken)
    {
        var lastSuccessAt = await db.BackupRuns.AsNoTracking()
            .Where(r => r.Succeeded)
            .OrderByDescending(r => r.FinishedAt)
            .Select(r => (DateTimeOffset?)r.FinishedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var lastRun = await db.BackupRuns.AsNoTracking()
            .OrderByDescending(r => r.FinishedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var lastRunFailed = lastRun is { Succeeded: false };
        return new BackupStatus(lastSuccessAt, lastRunFailed, lastRunFailed ? lastRun!.Error : null);
    }
}
