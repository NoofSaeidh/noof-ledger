using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Startup;

internal static class DatabaseGateRegistration
{
    public static IServiceCollection AddNoofDatabaseGate(this IServiceCollection services)
    {
        services.AddSingleton<DatabaseGate>();
        services.AddSingleton<IDatabaseGate>(sp => sp.GetRequiredService<DatabaseGate>());
        services.AddSingleton<IDatabaseStartupProbe, DatabaseStartupProbe>();
        services.AddHostedService<DatabaseStartupService>();

        return services;
    }
}
