using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Tests.Diagnostics;

sealed class FakeCheck(string name, int order, Func<CancellationToken, Task<HealthOutcome>> answer, string logCategory = "Noof.Ledger.Tests") : ISystemHealthCheck
{
    public int Runs { get; private set; }

    public string Name => name;

    public int Order => order;

    public string LogCategory => logCategory;

    public Task<HealthOutcome> CheckAsync(CancellationToken cancellationToken)
    {
        Runs++;
        return answer(cancellationToken);
    }
}
