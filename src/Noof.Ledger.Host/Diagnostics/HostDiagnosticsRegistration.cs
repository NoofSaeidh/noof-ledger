using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Diagnostics;

internal static class HostDiagnosticsRegistration
{
    public static IServiceCollection AddNoofHostDiagnostics(this IServiceCollection services, string connectionString)
    {
        var databasePassword = new Npgsql.NpgsqlConnectionStringBuilder(connectionString).Password ?? string.Empty;

        services.AddSingleton<LogSinkStatus>();
        services.AddSingleton<ILogSinkStatus>(sp => sp.GetRequiredService<LogSinkStatus>());
        services.AddSingleton(sp => new SecretSnapshot(sp.GetRequiredService<IServiceScopeFactory>(), databasePassword));
        services.AddSingleton<ISecretValueSource>(sp => sp.GetRequiredService<SecretSnapshot>());
        services.AddSingleton<SecretRedactor>();
        services.AddHostedService(sp => new SecretSnapshotRefreshWorker(
            sp.GetRequiredService<IDatabaseGate>(), sp.GetRequiredService<SecretSnapshot>(), sp.GetRequiredService<TimeProvider>()));

        return services;
    }
}
