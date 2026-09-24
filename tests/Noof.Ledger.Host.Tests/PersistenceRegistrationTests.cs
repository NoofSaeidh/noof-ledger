using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Application.Capture;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Jobs;
using Noof.Ledger.Application.Reporting;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Persistence;

namespace Noof.Ledger.Host.Tests;

public class PersistenceRegistrationTests
{
    [Fact]
    public void AddNoofPersistence_registers_every_store_the_application_asks_for()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(TimeZoneInfo.Utc);
        services.AddDataProtection();
        services.AddNoofPersistence(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Ledger"] = "Host=127.0.0.1;Database=noof_ledger_never_opened;Username=x;Password=y",
                })
                .Build(),
            maxJobAttempts: 8);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        // Resolving is enough: none of these opens a connection in its constructor, so this test never
        // reaches PostgreSQL. The database name is deliberately one that does not exist, so a future
        // constructor that DID connect would fail loudly here rather than touching a real ledger.
        scope.ServiceProvider.GetRequiredService<IUserStore>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<ISecretStore>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<ICaptureStore>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<ICategorizationStore>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<ICategoryCatalog>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<IMerchantDirectory>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<ISpendingReadModel>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<IBalanceReadModel>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<IJobQueue>().Should().NotBeNull();
    }
}
