using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Application.Reporting.Summary;
using Noof.Ledger.Persistence;

namespace Noof.Ledger.Host.Tests.Summary;

public class SummaryRegistrationTests
{
    [Fact]
    public void AddNoofApplication_registers_one_summary_text_for_the_whole_process()
    {
        var services = new ServiceCollection();
        services.AddNoofApplication(
            new SlowOperationOptions(),
            new FiscalVerificationUrlOptions { VerificationUrlPrefix = "https://suf.purs.gov.rs/v/?vl=" });

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IMonthlySummaryText>().Should().BeOfType<MonthlySummaryText>()
            .And.BeSameAs(provider.GetRequiredService<IMonthlySummaryText>());
    }

    static ServiceCollection WithPersistence()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(TimeZoneInfo.Utc);
        services.AddDataProtection();
        services.AddNoofApplication(
            new SlowOperationOptions(),
            new FiscalVerificationUrlOptions { VerificationUrlPrefix = "https://suf.purs.gov.rs/v/?vl=" });
        services.AddNoofPersistence(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Ledger"] = "Host=127.0.0.1;Database=noof_ledger_never_opened;Username=x;Password=y",
                })
                .Build(),
            maxJobAttempts: 8);
        return services;
    }

    [Fact]
    public void The_summary_and_net_worth_services_are_scoped_and_resolve_over_the_persistence_stores()
    {
        var services = WithPersistence();

        services.Should().ContainSingle(descriptor => descriptor.ServiceType == typeof(IMonthlySummaryService))
            .Which.Lifetime.Should().Be(ServiceLifetime.Scoped, "they read through scoped stores that share the scope's DbContext");
        services.Should().ContainSingle(descriptor => descriptor.ServiceType == typeof(INetWorthService))
            .Which.Lifetime.Should().Be(ServiceLifetime.Scoped);

        // Resolving opens no connection (the database name does not exist); ValidateScopes catches a singleton that
        // would capture a scoped store.
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMonthlySummaryService>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<INetWorthService>().Should().NotBeNull();
    }
}
