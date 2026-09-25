using Microsoft.Extensions.Diagnostics.HealthChecks;
using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Diagnostics;

internal sealed class LogSinkHealthCheck(IDatabaseGate gate, ILogSinkStatus sinkStatus, TimeProvider timeProvider) : IHealthCheck
{
    static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (gate.State is not DatabaseState.Ready)
            return Task.FromResult(HealthCheckResult.Degraded("Waiting for the database"));

        var result = sinkStatus.LastFailureAt is { } at && timeProvider.GetUtcNow() - at < Window
            ? HealthCheckResult.Degraded("Logs are in the file only")
            : HealthCheckResult.Healthy("Writing to the database");

        return Task.FromResult(result);
    }
}
