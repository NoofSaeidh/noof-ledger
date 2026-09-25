using Microsoft.Extensions.Diagnostics.HealthChecks;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Host.Workers;

namespace Noof.Ledger.Host.Diagnostics;

internal sealed class DiskHealthCheck(
    IDatabaseGate gate, IFreeSpaceProvider freeSpace, IConfiguration configuration, BackupWorkerOptions backupOptions) : IHealthCheck
{
    const long OkBytes = 5L * 1024 * 1024 * 1024;
    const long FailingBytes = 1L * 1024 * 1024 * 1024;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (gate.State is not DatabaseState.Ready)
            return Task.FromResult(HealthCheckResult.Degraded("Waiting for the database"));

        var logDirectory = Environment.ExpandEnvironmentVariables(
            configuration["Logging:File:Directory"]
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoofLedger", "logs"));

        var logFree = SafeFreeBytes(logDirectory);
        var backupFree = SafeFreeBytes(backupOptions.BackupDirectory);
        var worst = Math.Min(logFree, backupFree);

        var result = worst switch
        {
            < 0 => HealthCheckResult.Unhealthy("Could not read free disk space"),
            < FailingBytes => HealthCheckResult.Unhealthy($"{FormatGb(worst)} GB free"),
            < OkBytes => HealthCheckResult.Degraded($"{FormatGb(worst)} GB free"),
            _ => HealthCheckResult.Healthy($"{FormatGb(worst)} GB free"),
        };

        return Task.FromResult(result);
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
