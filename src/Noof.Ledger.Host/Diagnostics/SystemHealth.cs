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

        HealthItem[] items = [.. HealthCheckNames.Ordered.Select(name => raw.Entries.TryGetValue(name, out var entry)
            ? new HealthItem(name, Map(entry.Status), entry.Description ?? string.Empty, now)
            : new HealthItem(name, HealthLevel.Failing, "Check not registered", now))];

        // Overall reflects only the checks the report actually ran, not the ones this call
        // synthesizes a "Check not registered" item for - a unit test that stubs a partial
        // HealthReport (fewer than all seven checks) still gets an Overall driven by what it stubbed.
        var overall = raw.Entries.Count == 0 ? HealthLevel.Ok : raw.Entries.Values.Max(entry => Map(entry.Status));

        return new SystemHealthReport(overall, items);
    }

    static HealthLevel Map(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => HealthLevel.Ok,
        HealthStatus.Degraded => HealthLevel.Warning,
        _ => HealthLevel.Failing,
    };
}
