using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Noof.Ledger.Host.Workers;

namespace Noof.Ledger.Host.Tests;

// Every test host swaps IFxRateSource for a stub (TestHostRates), so the host's own call to AddNoofFx is proven by
// what the stub cannot hide: the open-er-api named client it registers.
public class FxRateWiringTests
{
    static WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseTempLogDirectory();
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("Backup:Enabled", "false");
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
        });

    [Fact]
    public void The_host_registers_the_open_er_api_client()
    {
        using var factory = Factory();

        var client = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient("open-er-api");

        client.BaseAddress.Should().Be(new Uri("https://open.er-api.com/"));
    }

    [Fact]
    public void FxRateWorker_is_registered_as_a_hosted_service()
    {
        using var factory = Factory();

        factory.Services.GetServices<IHostedService>().Should().Contain(service => service is FxRateWorker);
    }
}
