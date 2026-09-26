using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Diagnostics;

internal static class DiagnosticsRegistration
{
    public static IServiceCollection AddNoofDiagnostics(this IServiceCollection services)
    {
        services.AddSingleton<IPollingHeartbeat, PollingHeartbeat>();
        services.AddSingleton<IFreeSpaceProvider, DriveFreeSpaceProvider>();
        services.AddSingleton<ISystemHealth, SystemHealth>();

        services.AddScoped<ISystemHealthCheck, DatabaseHealthCheck>();
        services.AddScoped<ISystemHealthCheck, BackupHealthCheck>();
        services.AddScoped<ISystemHealthCheck, DiskHealthCheck>();
        services.AddScoped<ISystemHealthCheck, LogSinkHealthCheck>();

        return services;
    }
}
