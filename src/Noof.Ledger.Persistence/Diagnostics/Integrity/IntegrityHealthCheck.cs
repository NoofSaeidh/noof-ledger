using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Diagnostics.Integrity;

namespace Noof.Ledger.Persistence.Diagnostics.Integrity;

// A Bug turns the tile red, anything waiting on the operator amber (spec §1 "Health"). The whole integrity run counts
// against this one check's 5 s, and while the database is not ready it answers without touching it, as Migrations
// does (P-8).
internal sealed class IntegrityHealthCheck(IIntegrityChecks checks, IDatabaseGate gate) : ISystemHealthCheck
{
    public string Name => IntegrityHealth.CheckName;

    public int Order => 90;

    public string LogCategory => "Noof.Ledger.Persistence.Diagnostics.Integrity";

    public async Task<HealthOutcome> CheckAsync(CancellationToken cancellationToken)
    {
        if (gate.State is not DatabaseState.Ready)
            return HealthOutcome.Warning("Waiting for the database");

        var findings = await checks.FindAllAsync(cancellationToken);
        var bugs = findings.Count(finding => finding.Group == IntegrityGroup.Bug);

        return (bugs, findings.Count) switch
        {
            (1, _) => HealthOutcome.Failing("1 bug found"),
            (> 1, _) => HealthOutcome.Failing($"{bugs} bugs found"),
            (_, 0) => HealthOutcome.Ok("No findings"),
            (_, 1) => HealthOutcome.Warning("1 waiting on you"),
            (_, var waiting) => HealthOutcome.Warning($"{waiting} waiting on you"),
        };
    }
}
