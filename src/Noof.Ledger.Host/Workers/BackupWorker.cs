using Noof.Ledger.Application.Backup;

namespace Noof.Ledger.Host.Workers;

internal enum BackupTickResult { BackedUp, Skipped, Failed }

// TimeProvider-driven throughout (CLAUDE.md "seed timestamps from fixed literals"): the started/
// finished stamps recorded in backup_runs and the file name both come from timeProvider, never
// DateTimeOffset.UtcNow, so a test can assert an exact file name and an exact recorded interval.
internal sealed class BackupWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    BackupWorkerOptions options,
    ILogger<BackupWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var result = await RunTickAsync(stoppingToken);
            var delay = result == BackupTickResult.Failed ? options.RetryInterval : options.Interval;
            await Task.Delay(delay, timeProvider, stoppingToken);
        }
    }

    // Never throws (B1/B2: "a failure is logged and recorded, never crashes the host") - every
    // failure path below, including one this method cannot foresee, is caught and turned into a
    // recorded run instead of an unhandled exception on the host's background-service thread.
    public async Task<BackupTickResult> RunTickAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var backupLog = scope.ServiceProvider.GetRequiredService<IBackupLog>();

            var status = await backupLog.StatusAsync(cancellationToken);
            var now = timeProvider.GetUtcNow();
            if (status.LastSuccessAt is { } lastSuccess && now - lastSuccess < options.Interval)
                return BackupTickResult.Skipped;

            var dumper = scope.ServiceProvider.GetRequiredService<IDatabaseDumper>();
            return await RunBackupAsync(backupLog, dumper, now, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Backup worker tick failed");
            return BackupTickResult.Failed;
        }
    }

    async Task<BackupTickResult> RunBackupAsync(
        IBackupLog backupLog, IDatabaseDumper dumper, DateTimeOffset started, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.BackupDirectory);
        var fileName = $"noof_ledger-{started:yyyyMMdd-HHmmss}.dump";
        var finalPath = Path.Combine(options.BackupDirectory, fileName);

        // Dumped under a temporary name and renamed only on success, so a half-written dump - the
        // process killed mid-write, the disk filling up - never looks like a finished one to
        // anything (restore-check, a future prune) that lists this directory (B1).
        var tempPath = finalPath + ".tmp";

        DumpResult result;
        try
        {
            result = await dumper.DumpAsync(tempPath, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new DumpResult(false, ex.Message);
        }

        var finished = timeProvider.GetUtcNow();
        long? size = null;

        if (result.Succeeded && File.Exists(tempPath))
        {
            File.Move(tempPath, finalPath, overwrite: true);
            size = new FileInfo(finalPath).Length;
            Prune();
        }
        else if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }

        await backupLog.RecordAsync(
            new BackupRunRecord(started, finished, result.Succeeded, result.Succeeded ? fileName : null, size, result.Error),
            cancellationToken);

        if (!result.Succeeded)
            logger.LogError("Backup failed: {Error}", result.Error);

        return result.Succeeded ? BackupTickResult.BackedUp : BackupTickResult.Failed;
    }

    void Prune()
    {
        var fileNames = Directory.EnumerateFiles(options.BackupDirectory, "noof_ledger-*.dump")
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!)
            .ToArray();

        foreach (var name in BackupRetention.ToDelete(fileNames, options.KeepCount))
            File.Delete(Path.Combine(options.BackupDirectory, name));
    }
}
