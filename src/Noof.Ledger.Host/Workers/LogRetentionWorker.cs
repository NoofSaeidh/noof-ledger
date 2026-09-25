using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Workers;

internal sealed partial class LogRetentionWorker(
    IServiceScopeFactory scopeFactory, IDatabaseGate gate, TimeProvider timeProvider, ILogger<LogRetentionWorker> logger)
    : BackgroundService
{
    static readonly TimeSpan FirstRunDelay = TimeSpan.FromSeconds(60);
    static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await gate.WaitUntilReadyAsync(stoppingToken);
        await Task.Delay(FirstRunDelay, timeProvider, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunPruneAsync(stoppingToken);
            await Task.Delay(Interval, timeProvider, stoppingToken);
        }
    }

    async Task RunPruneAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var retention = scope.ServiceProvider.GetRequiredService<ILogRetention>();
            var pruned = await retention.PruneAsync(cancellationToken);
            LogPruned(pruned);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPruneFailed(ex);
        }
    }

    [LoggerMessage(EventId = 5201, Level = LogLevel.Information, Message = "Log retention pruned {PrunedCount} rows")]
    partial void LogPruned(int prunedCount);

    [LoggerMessage(EventId = 5202, Level = LogLevel.Error, Message = "Log retention prune failed")]
    partial void LogPruneFailed(Exception exception);
}
