using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Diagnostics;

internal sealed class SystemHealth(
    IServiceScopeFactory scopeFactory, TimeProvider timeProvider, ILoggerFactory loggerFactory,
    IOperationTimer timer, ILogger<SystemHealth> logger) : ISystemHealth
{
    static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(5);

    readonly Lock gate = new();
    SystemHealthReport? cached;
    DateTimeOffset cachedAt;

    public async Task<SystemHealthReport> GetAsync(bool fresh, CancellationToken cancellationToken)
    {
        if (!fresh)
        {
            lock (gate)
            {
                if (cached is { } current && timeProvider.GetUtcNow() - cachedAt < CacheDuration)
                    return current;
            }
        }

        var report = await RunAsync(cancellationToken);

        lock (gate)
        {
            cached = report;
            cachedAt = timeProvider.GetUtcNow();
        }

        return report;
    }

    // The checks share this one scope's LedgerDbContext, which refuses a second concurrent
    // operation - hence one at a time.
    async Task<SystemHealthReport> RunAsync(CancellationToken cancellationToken)
    {
        using var running = timer.Start(logger, TimedOperations.HealthRun);

        await using var scope = scopeFactory.CreateAsyncScope();
        var checks = scope.ServiceProvider.GetServices<ISystemHealthCheck>()
            .OrderBy(check => check.Order).ThenBy(check => check.Name, StringComparer.Ordinal);

        List<(ISystemHealthCheck Check, HealthOutcome Outcome)> results = [];
        foreach (var check in checks)
            results.Add((check, await OutcomeOfAsync(check, cancellationToken)));

        var now = timeProvider.GetUtcNow();
        HealthItem[] items = [.. results.Select(
            r => new HealthItem(r.Check.Name, r.Outcome.Level, r.Outcome.Summary, now, r.Check.LogCategory))];

        var overall = items.Length == 0 ? HealthLevel.Ok : items.Max(item => item.Level);

        return new SystemHealthReport(overall, items);
    }

    async Task<HealthOutcome> OutcomeOfAsync(ISystemHealthCheck check, CancellationToken cancellationToken)
    {
        // Logged under SystemHealth's own category, not the check's: a checked-in Override on the
        // check's category (e.g. Microsoft.EntityFrameworkCore=Information for Migrations) would
        // otherwise drop this Debug timing.
        using var timing = timer.Start(logger, TimedOperations.HealthCheck(check.Name));

        using var timeout = new CancellationTokenSource(CheckTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var checkLogger = loggerFactory.CreateLogger(check.LogCategory);

        try
        {
            return await check.CheckAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            checkLogger.HealthCheckTimedOut(check.Name, (int)CheckTimeout.TotalSeconds);
            return HealthOutcome.Failing($"No answer within {(int)CheckTimeout.TotalSeconds} s");
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            checkLogger.HealthCheckFailed(check.Name, exception);
            return HealthOutcome.Failing($"Check failed ({exception.GetType().Name}) — see logs");
        }
    }
}
