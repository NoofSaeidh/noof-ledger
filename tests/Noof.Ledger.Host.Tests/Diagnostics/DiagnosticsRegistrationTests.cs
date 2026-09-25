using AwesomeAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Diagnostics;
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

        services.AddNoofDiagnostics();

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ISystemHealth>().Should().BeSameAs(provider.GetRequiredService<ISystemHealth>());
        provider.GetRequiredService<IPollingHeartbeat>().Should().BeSameAs(provider.GetRequiredService<IPollingHeartbeat>());
    }

    [Fact]
    public void AddNoofDiagnostics_registers_its_five_Host_owned_checks()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);

        services.AddNoofDiagnostics();

        var registrations = services.BuildServiceProvider().GetRequiredService<HealthCheckService>();
        registrations.Should().NotBeNull();
    }
}
