using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Diagnostics;

internal static class DiagnosticsRegistration
{
    public static IServiceCollection AddNoofDiagnostics(this IServiceCollection services)
    {
        services.AddSingleton<IPollingHeartbeat, PollingHeartbeat>();
        services.AddSingleton<IFreeSpaceProvider, DriveFreeSpaceProvider>();
        services.AddSingleton<ISystemHealth, SystemHealth>();

        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>(HealthCheckNames.Database)
            .AddCheck<AiKeysHealthCheck>(HealthCheckNames.AiKeys)
            .AddCheck<BackupHealthCheck>(HealthCheckNames.Backup)
            .AddCheck<DiskHealthCheck>(HealthCheckNames.Disk)
            .AddCheck<LogSinkHealthCheck>(HealthCheckNames.LogSink);

        return services;
    }
}
