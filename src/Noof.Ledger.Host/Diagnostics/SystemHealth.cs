using Microsoft.Extensions.Diagnostics.HealthChecks;
using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Diagnostics;

internal sealed class SystemHealth(HealthCheckService healthCheckService, TimeProvider timeProvider) : ISystemHealth
{
    static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

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

    async Task<SystemHealthReport> RunAsync(CancellationToken cancellationToken)
    {
        var raw = await healthCheckService.CheckHealthAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();

        HealthItem[] items = [.. HealthCheckNames.Ordered.Select(name =>
        {
            var logCategory = HealthCheckLogCategories.ByCheckName.GetValueOrDefault(name, string.Empty);
            return raw.Entries.TryGetValue(name, out var entry)
                ? new HealthItem(name, Map(entry.Status), entry.Description ?? string.Empty, now, logCategory)
                : new HealthItem(name, HealthLevel.Failing, "Check not registered", now, logCategory);
        })];

        // Overall is the worst of the items exactly as displayed, so a check missing from
        // registration - shown as Failing, "Check not registered" - counts toward it the same way.
        var overall = items.Length == 0 ? HealthLevel.Ok : items.Max(item => item.Level);

        return new SystemHealthReport(overall, items);
    }

    static HealthLevel Map(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => HealthLevel.Ok,
        HealthStatus.Degraded => HealthLevel.Warning,
        _ => HealthLevel.Failing,
    };
}
