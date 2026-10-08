using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Fx;
using Noof.Ledger.Host.Workers.FxRateLogging;

namespace Noof.Ledger.Host.Workers;

internal enum FxRateTickResult { Unknown = 0, UpToDate = 1, Stored = 2, AlreadyStored = 3, NothingFetched = 4, Failed = 5 }

// Spec P-2: open.er-api.com publishes a day's rates just after 00:00 UTC, so an archive that holds UTC today's has
// nothing newer to fetch: a running host asks every tick until today's are in, then not until tomorrow, and one whose
// archive is fresh - the demo's and the E2E clone's seeded ones - never calls out.
internal sealed class FxRateWorker(
    IServiceScopeFactory scopeFactory, TimeProvider timeProvider, IDatabaseGate gate, ILogger<FxRateWorker> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await gate.WaitUntilReadyAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunTickAsync(stoppingToken);
            await Task.Delay(Interval, timeProvider, stoppingToken);
        }
    }

    public async Task<FxRateTickResult> RunTickAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IFxRateStore>();

            if (await store.NewestAsOfDateAsync(cancellationToken) is { } newest && newest >= UtcToday())
                return FxRateTickResult.UpToDate;

            var source = scope.ServiceProvider.GetRequiredService<IFxRateSource>();
            if (await source.FetchLatestAsync(cancellationToken) is not { } snapshot)
            {
                logger.NothingFetched();
                return FxRateTickResult.NothingFetched;
            }

            if (!await store.AppendAsync(snapshot, cancellationToken))
            {
                logger.RatesAlreadyStored(snapshot.AsOfDate);
                return FxRateTickResult.AlreadyStored;
            }

            logger.RatesStored(snapshot.AsOfDate, snapshot.UnitsPerEur.Count);
            return FxRateTickResult.Stored;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.TickFailed(ex);
            return FxRateTickResult.Failed;
        }
    }

    DateOnly UtcToday() => DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
}
