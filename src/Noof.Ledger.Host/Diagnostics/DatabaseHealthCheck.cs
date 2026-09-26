using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Diagnostics;

internal sealed class DatabaseHealthCheck(IDatabaseGate gate) : ISystemHealthCheck
{
    public string Name => "Database";

    public int Order => 10;

    public string LogCategory => "Noof.Ledger.Host.Startup.DatabaseStartupService";

    public Task<HealthOutcome> CheckAsync(CancellationToken cancellationToken)
    {
        var result = gate.State switch
        {
            DatabaseState.Ready => HealthOutcome.Ok("Ready"),
            DatabaseState.Waiting or DatabaseState.Migrating =>
                HealthOutcome.Warning("Offline — waiting for PostgreSQL"),
            DatabaseState.Failed => HealthOutcome.Failing(gate.Detail ?? "Migration failed"),
            _ => HealthOutcome.Failing("Unknown database state"),
        };

        return Task.FromResult(result);
    }
}
