using Noof.Ledger.Application.Backup;
using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Diagnostics;

internal sealed class BackupHealthCheck(IDatabaseGate gate, IBackupLog backupLog, TimeProvider timeProvider) : ISystemHealthCheck
{
    static readonly TimeSpan FreshWindow = TimeSpan.FromHours(26);

    public string Name => "Backup";

    public int Order => 50;

    public string LogCategory => "Noof.Ledger.Host.Workers.BackupWorker";

    public async Task<HealthOutcome> CheckAsync(CancellationToken cancellationToken)
    {
        if (gate.State is not DatabaseState.Ready)
            return HealthOutcome.Warning("Waiting for the database");

        var status = await backupLog.StatusAsync(cancellationToken);

        if (status.LastRunFailed)
        {
            return HealthOutcome.Warning(status.LastSuccessAt is { } at
                ? $"Last run failed — last success {Describe(timeProvider.GetUtcNow() - at)} ago"
                : "Last run failed");
        }

        if (status.LastSuccessAt is not { } lastSuccess)
            return HealthOutcome.Warning("No backup has ever succeeded");

        var age = timeProvider.GetUtcNow() - lastSuccess;
        return age < FreshWindow
            ? HealthOutcome.Ok($"Last success {Describe(age)} ago")
            : HealthOutcome.Warning($"Stale — last success {Describe(age)} ago");
    }

    static string Describe(TimeSpan elapsed) => elapsed switch
    {
        { TotalMinutes: < 1 } => "just now",
        { TotalHours: < 1 } => $"{(int)elapsed.TotalMinutes} min",
        { TotalDays: < 1 } => $"{(int)elapsed.TotalHours} h",
        _ => $"{(int)elapsed.TotalDays} d",
    };
}
