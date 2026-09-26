using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Diagnostics;

internal sealed partial class SecretSnapshotRefreshWorker(
    IDatabaseGate gate, SecretSnapshot snapshot, TimeProvider timeProvider, ILogger<SecretSnapshotRefreshWorker> logger)
    : BackgroundService
{
    static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await gate.WaitUntilReadyAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await snapshot.RefreshAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRefreshFailed(ex);
            }

            await Task.Delay(RefreshInterval, timeProvider, stoppingToken);
        }
    }

    [LoggerMessage(EventId = 5103, Level = LogLevel.Warning, Message = "Secret snapshot refresh failed; keeping the previous snapshot")]
    partial void LogRefreshFailed(Exception exception);
}
