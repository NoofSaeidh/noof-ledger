using Microsoft.Extensions.Diagnostics.HealthChecks;
using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Diagnostics;

internal sealed class DatabaseHealthCheck(IDatabaseGate gate) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var result = gate.State switch
        {
            DatabaseState.Ready => HealthCheckResult.Healthy("Ready"),
            DatabaseState.Waiting or DatabaseState.Migrating =>
                HealthCheckResult.Degraded("Offline — waiting for PostgreSQL"),
            DatabaseState.Failed => HealthCheckResult.Unhealthy(gate.Detail ?? "Migration failed"),
            _ => HealthCheckResult.Unhealthy("Unknown database state"),
        };

        return Task.FromResult(result);
    }
}
