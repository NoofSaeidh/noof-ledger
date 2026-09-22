using Microsoft.Extensions.DependencyInjection;

namespace Noof.Ledger.Host.Workers;

internal static class WorkerRegistration
{
    public static IServiceCollection AddNoofWorkers(
        this IServiceCollection services, CategorizationWorkerOptions options)
    {
        services.AddSingleton(options);
        services.AddHostedService(sp => new CategorizationWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            options,
            CategorizationWorker.CreateWorkerId(),
            sp.GetRequiredService<ILogger<CategorizationWorker>>()));

        return services;
    }
}
