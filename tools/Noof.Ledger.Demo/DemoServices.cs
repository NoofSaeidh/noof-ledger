using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Host.Auth;
using Noof.Ledger.Host.Startup;
using Noof.Ledger.Persistence;

namespace Noof.Ledger.Demo;

internal static class DemoServices
{
    const int MaxJobAttempts = 8;

    // Seeding keeps the fixed mock now; a test of what the demo host shows passes the real clock, which the host runs on.
    public static ServiceProvider Build(string connectionString, DemoPaths paths, TimeProvider? clock = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Ledger"] = connectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(clock ?? new FixedClock(MockData.Now));
        services.AddSingleton(CaptureTimeZoneGuard.Resolve(MockData.TimeZoneId));
        DataProtectionSetup.Configure(services, new DirectoryInfo(paths.KeyRing));
        services.AddSingleton<IPasswordHasher, PasswordHasherAdapter>();
        services.AddNoofApplication(
            new SlowOperationOptions(),
            new FiscalVerificationUrlOptions { VerificationUrlPrefix = "https://suf.purs.gov.rs/v/?vl=" });
        services.AddNoofPersistence(configuration, MaxJobAttempts);

        return services.BuildServiceProvider();
    }
}
