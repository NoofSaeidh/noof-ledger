using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Persistence.Diagnostics;

internal sealed class MigrationsHealthCheck(LedgerDbContext db, IDatabaseGate gate) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (gate.State is not DatabaseState.Ready)
            return HealthCheckResult.Degraded("Waiting for the database");

        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();

        return pending.Length == 0
            ? HealthCheckResult.Healthy("Up to date")
            : HealthCheckResult.Unhealthy($"{pending.Length} migration(s) pending");
    }
}
