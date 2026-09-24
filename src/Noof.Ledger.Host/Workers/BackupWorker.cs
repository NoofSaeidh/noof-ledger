using Noof.Ledger.Application.Backup;
using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Workers;

internal enum BackupTickResult { BackedUp, Skipped, Failed }

internal readonly record struct BackupTickOutcome(BackupTickResult Result, DateTimeOffset? LastSuccessAt);

// TimeProvider-driven throughout (CLAUDE.md "seed timestamps from fixed literals"): the started/
// finished stamps recorded in backup_runs and the file name both come from timeProvider, never
// DateTimeOffset.UtcNow, so a test can assert an exact file name and an exact recorded interval.
internal sealed class BackupWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    BackupWorkerOptions options,
    IDatabaseGate gate,
    ILogger<BackupWorker> logger)
    : BackgroundService
{
    // A tick this close to due wakes at the floor instead of the few seconds Interval minus
    // elapsed would otherwise compute to - never a tight loop re-checking a database connection
    // every few seconds while a backup is not actually due yet.
    static readonly TimeSpan MinimumSkipDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await gate.WaitUntilReadyAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var outcome = await RunTickCoreAsync(stoppingToken);
            await Task.Delay(DelayUntilNextTick(outcome), timeProvider, stoppingToken);
        }
    }

    // Never throws (B1/B2: "a failure is logged and recorded, never crashes the host") - every
    // failure path below, including one this method cannot foresee, is caught and turned into a
    // recorded run instead of an unhandled exception on the host's background-service thread.
    public async Task<BackupTickResult> RunTickAsync(CancellationToken cancellationToken) =>
        (await RunTickCoreAsync(cancellationToken)).Result;

    // I-2 (Phase 4 final review): a Skipped tick used to sleep a full Interval from *now*, so a
    // host restarted well into the current window (e.g. 20 h after the last success) would not
    // check again for another 24 h instead of the ~4 h actually remaining - the cadence degrades
    // to every other day on a machine that is not always on.
    TimeSpan DelayUntilNextTick(BackupTickOutcome outcome) => outcome.Result switch
    {
        BackupTickResult.Failed => options.RetryInterval,
        BackupTickResult.Skipped when outcome.LastSuccessAt is { } lastSuccess =>
            Max(options.Interval - (timeProvider.GetUtcNow() - lastSuccess), MinimumSkipDelay),
        _ => options.Interval,
    };

    static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    async Task<BackupTickOutcome> RunTickCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var backupLog = scope.ServiceProvider.GetRequiredService<IBackupLog>();

            var status = await backupLog.StatusAsync(cancellationToken);
            var now = timeProvider.GetUtcNow();
            if (status.LastSuccessAt is { } lastSuccess && now - lastSuccess < options.Interval)
                return new BackupTickOutcome(BackupTickResult.Skipped, lastSuccess);

            var dumper = scope.ServiceProvider.GetRequiredService<IDatabaseDumper>();
            var result = await RunBackupAsync(backupLog, dumper, now, cancellationToken);
            return new BackupTickOutcome(result, status.LastSuccessAt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.BackupTickFailed(ex);
            return new BackupTickOutcome(BackupTickResult.Failed, null);
        }
    }

    async Task<BackupTickResult> RunBackupAsync(
        IBackupLog backupLog, IDatabaseDumper dumper, DateTimeOffset started, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.BackupDirectory);
        DeleteOrphanTempFiles();
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
            logger.BackupFailed(result.Error);

        return result.Succeeded ? BackupTickResult.BackedUp : BackupTickResult.Failed;
    }

    // M-4 (Phase 4 final review): a cancelled dump used to leave its .tmp file behind forever -
    // Prune only ever matches *.dump, and RunBackupAsync's own cleanup never runs when the dump
    // itself throws OperationCanceledException. Runs are serial (this worker is a single
    // BackgroundService looping one tick at a time), so any *.tmp found here can only be a
    // leftover from an earlier run that never finished, never one currently in progress.
    void DeleteOrphanTempFiles()
    {
        foreach (var path in Directory.EnumerateFiles(options.BackupDirectory, "*.tmp"))
            File.Delete(path);
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

// A sibling top-level static class, not nested inside BackupWorker: a [LoggerMessage] extension
// method nested inside a non-static class fails to compile here with CS1109 ("Extension methods
// must be defined in a top level static class"), verified by a clean rebuild, not by documentation.
internal static partial class BackupWorkerLog
{
    [LoggerMessage(EventId = 1101, Level = LogLevel.Error, Message = "Backup worker tick failed")]
    public static partial void BackupTickFailed(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Error, Message = "Backup failed: {Error}")]
    public static partial void BackupFailed(this ILogger logger, string? error);
}
