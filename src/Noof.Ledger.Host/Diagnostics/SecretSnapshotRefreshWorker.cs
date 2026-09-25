using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Diagnostics;

internal sealed class SecretSnapshotRefreshWorker(IDatabaseGate gate, SecretSnapshot snapshot, TimeProvider timeProvider)
    : BackgroundService
{
    static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await gate.WaitUntilReadyAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await snapshot.RefreshAsync(stoppingToken);
            await Task.Delay(RefreshInterval, timeProvider, stoppingToken);
        }
    }
}
