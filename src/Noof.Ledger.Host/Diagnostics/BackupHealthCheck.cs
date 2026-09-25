using Microsoft.Extensions.Diagnostics.HealthChecks;
using Noof.Ledger.Application.Backup;
using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Diagnostics;

internal sealed class BackupHealthCheck(IDatabaseGate gate, IBackupLog backupLog, TimeProvider timeProvider) : IHealthCheck
{
    static readonly TimeSpan FreshWindow = TimeSpan.FromHours(26);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (gate.State is not DatabaseState.Ready)
            return HealthCheckResult.Degraded("Waiting for the database");

        var status = await backupLog.StatusAsync(cancellationToken);

        if (status.LastRunFailed)
        {
            return HealthCheckResult.Degraded(status.LastSuccessAt is { } at
                ? $"Last run failed — last success {Describe(timeProvider.GetUtcNow() - at)} ago"
                : "Last run failed");
        }

        if (status.LastSuccessAt is not { } lastSuccess)
            return HealthCheckResult.Degraded("No backup has ever succeeded");

        var age = timeProvider.GetUtcNow() - lastSuccess;
        return age < FreshWindow
            ? HealthCheckResult.Healthy($"Last success {Describe(age)} ago")
            : HealthCheckResult.Degraded($"Stale — last success {Describe(age)} ago");
    }

    static string Describe(TimeSpan elapsed) => elapsed switch
    {
        { TotalMinutes: < 1 } => "just now",
        { TotalHours: < 1 } => $"{(int)elapsed.TotalMinutes} min",
        { TotalDays: < 1 } => $"{(int)elapsed.TotalHours} h",
        _ => $"{(int)elapsed.TotalDays} d",
    };
}
