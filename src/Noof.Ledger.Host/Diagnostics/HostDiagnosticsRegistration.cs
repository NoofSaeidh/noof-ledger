using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Host.Logging;

namespace Noof.Ledger.Host.Diagnostics;

internal static class HostDiagnosticsRegistration
{
    public static IServiceCollection AddNoofHostDiagnostics(this IServiceCollection services, string connectionString)
    {
        var databasePassword = DatabasePassword.From(connectionString);

        services.AddSingleton<LogLevelSwitches>();
        services.AddSingleton<LogSinkStatus>();
        services.AddSingleton<ILogSinkStatus>(sp => sp.GetRequiredService<LogSinkStatus>());
        services.AddSingleton(sp => new SecretSnapshot(sp.GetRequiredService<IServiceScopeFactory>(), databasePassword));
        services.AddSingleton<ISecretValueSource>(sp => sp.GetRequiredService<SecretSnapshot>());
        services.AddSingleton<SecretRedactor>();
        services.AddHostedService(sp => new SecretSnapshotRefreshWorker(
            sp.GetRequiredService<IDatabaseGate>(), sp.GetRequiredService<SecretSnapshot>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<SecretSnapshotRefreshWorker>>()));

        services.AddSingleton<DatabaseLogLevel>();
        services.AddSingleton<IDatabaseLogLevel>(sp => sp.GetRequiredService<DatabaseLogLevel>());
        services.AddHostedService<DatabaseLogLevelLoader>();
        services.AddSingleton<IFileLogSinkInfo>(sp => new FileLogSinkInfo(sp.GetRequiredService<IConfiguration>()));

        return services;
    }
}
