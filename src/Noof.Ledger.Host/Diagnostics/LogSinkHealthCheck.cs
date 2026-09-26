using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Diagnostics;

internal sealed class LogSinkHealthCheck(IDatabaseGate gate, ILogSinkStatus sinkStatus, TimeProvider timeProvider) : ISystemHealthCheck
{
    static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    public string Name => "Log sink";

    public int Order => 70;

    public string LogCategory => "Noof.Ledger.Host.Logging";

    public Task<HealthOutcome> CheckAsync(CancellationToken cancellationToken)
    {
        if (gate.State is not DatabaseState.Ready)
            return Task.FromResult(HealthOutcome.Warning("Waiting for the database"));

        var result = sinkStatus.LastFailureAt is { } at && timeProvider.GetUtcNow() - at < Window
            ? HealthOutcome.Warning("Logs are in the file only")
            : HealthOutcome.Ok("Writing to the database");

        return Task.FromResult(result);
    }
}
