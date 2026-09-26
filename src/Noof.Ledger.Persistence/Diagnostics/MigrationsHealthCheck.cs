using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Persistence.Diagnostics;

internal sealed class MigrationsHealthCheck(LedgerDbContext db, IDatabaseGate gate) : ISystemHealthCheck
{
    public string Name => "Migrations";

    public int Order => 20;

    public string LogCategory => DbLoggerCategory.Migrations.Name;

    public async Task<HealthOutcome> CheckAsync(CancellationToken cancellationToken)
    {
        if (gate.State is not DatabaseState.Ready)
            return HealthOutcome.Warning("Waiting for the database");

        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();

        return pending.Length == 0
            ? HealthOutcome.Ok("Up to date")
            : HealthOutcome.Failing($"{pending.Length} migration(s) pending");
    }
}
