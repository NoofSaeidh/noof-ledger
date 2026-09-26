using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Tests.Diagnostics;

sealed class ScopeProbe
{
    public Guid Id { get; } = Guid.NewGuid();
}

sealed class ProbeCheck(ScopeProbe probe, string name, int order) : ISystemHealthCheck
{
    public string Name => name;

    public int Order => order;

    public string LogCategory => "Noof.Ledger.Tests";

    public Task<HealthOutcome> CheckAsync(CancellationToken cancellationToken) =>
        Task.FromResult(HealthOutcome.Ok(probe.Id.ToString()));
}
