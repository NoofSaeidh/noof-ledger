using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Logging;

internal sealed class DatabaseLogLevelLoader(
    IDatabaseGate gate, DatabaseLogLevel logLevel, TimeProvider timeProvider, ILogger<DatabaseLogLevelLoader> logger)
    : BackgroundService
{
    static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await gate.WaitUntilReadyAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await logLevel.LoadAsync(stoppingToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.DatabaseLogLevelLoadFailed(ex, RetryDelay);
            }

            await Task.Delay(RetryDelay, timeProvider, stoppingToken);
        }
    }
}
