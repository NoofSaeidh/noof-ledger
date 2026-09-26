using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Host.Logging;
using Noof.Ledger.Host.Workers;

namespace Noof.Ledger.Host.Diagnostics;

internal sealed class DiskHealthCheck(
    IDatabaseGate gate, IFreeSpaceProvider freeSpace, IConfiguration configuration, BackupWorkerOptions backupOptions) : ISystemHealthCheck
{
    const long OkBytes = 5L * 1024 * 1024 * 1024;
    const long FailingBytes = 1L * 1024 * 1024 * 1024;

    public string Name => "Disk";

    public int Order => 60;

    public string LogCategory => "Noof.Ledger.Host.Diagnostics";

    public async Task<HealthOutcome> CheckAsync(CancellationToken cancellationToken)
    {
        if (gate.State is not DatabaseState.Ready)
            return HealthOutcome.Warning("Waiting for the database");

        var logDirectory = LoggingSetup.ResolveLogDirectory(configuration);

        // DriveInfo has no cancellable API, so reading it off-thread lets the caller's timeout
        // abandon a stalled volume.
        var worst = await Task.Run(
            () => Math.Min(SafeFreeBytes(logDirectory), SafeFreeBytes(backupOptions.BackupDirectory)),
            cancellationToken).WaitAsync(cancellationToken);

        return worst switch
        {
            < 0 => HealthOutcome.Failing("Could not read free disk space"),
            < FailingBytes => HealthOutcome.Failing($"{FormatGb(worst)} GB free"),
            < OkBytes => HealthOutcome.Warning($"{FormatGb(worst)} GB free"),
            _ => HealthOutcome.Ok($"{FormatGb(worst)} GB free"),
        };
    }

    long SafeFreeBytes(string path)
    {
        try
        {
            return freeSpace.GetAvailableFreeBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    static string FormatGb(long bytes) => (bytes / 1024d / 1024d / 1024d).ToString("N1");
}
