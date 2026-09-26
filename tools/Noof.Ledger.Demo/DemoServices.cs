using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Host.Auth;
using Noof.Ledger.Host.Startup;
using Noof.Ledger.Persistence;

namespace Noof.Ledger.Demo;

internal static class DemoServices
{
    const int MaxJobAttempts = 8;

    public static ServiceProvider Build(string connectionString, DemoPaths paths)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Ledger"] = connectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<TimeProvider>(new FixedClock(MockData.Now));
        services.AddSingleton(CaptureTimeZoneGuard.Resolve(MockData.TimeZoneId));
        DataProtectionSetup.Configure(services, new DirectoryInfo(paths.KeyRing));
        services.AddSingleton<IPasswordHasher, PasswordHasherAdapter>();
        services.AddNoofPersistence(configuration, MaxJobAttempts);

        return services.BuildServiceProvider();
    }
}
