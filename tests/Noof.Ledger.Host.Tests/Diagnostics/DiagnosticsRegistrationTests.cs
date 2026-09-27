using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Host.Diagnostics;

namespace Noof.Ledger.Host.Tests.Diagnostics;

public class DiagnosticsRegistrationTests
{
    [Fact]
    public void AddNoofDiagnostics_registers_ISystemHealth_and_IPollingHeartbeat_as_singletons()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddNoofApplication(
            new SlowOperationOptions(),
            new FiscalVerificationUrlOptions { VerificationUrlPrefix = "https://suf.purs.gov.rs/v/?vl=" });

        services.AddNoofDiagnostics();

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ISystemHealth>().Should().BeSameAs(provider.GetRequiredService<ISystemHealth>());
        provider.GetRequiredService<IPollingHeartbeat>().Should().BeSameAs(provider.GetRequiredService<IPollingHeartbeat>());
    }
}
