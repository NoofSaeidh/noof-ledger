using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Fx;

namespace Noof.Ledger.Host.Tests;

public class TestHostRatesTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Built the way every Host.Tests factory is: the shared helper is the only rate-source override, so if a host
    // could reach the real source, so could this one.
    [Fact]
    public void A_test_host_resolves_only_the_stub_rate_source()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseTempLogDirectory();
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("Backup:Enabled", "false");
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
        });
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetServices<IFxRateSource>().Should().ContainSingle()
            .Which.Should().BeOfType<NoRemoteRateSource>("no test host may call open.er-api.com (spec P-2)");
    }

    [Fact]
    public async Task The_stub_fetches_nothing()
    {
        (await new NoRemoteRateSource().FetchLatestAsync(Ct)).Should().BeNull();
    }
}
