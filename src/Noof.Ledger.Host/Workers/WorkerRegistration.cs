
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;

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
            sp.GetRequiredService<IProposalMapper>(),
            sp.GetRequiredService<IMerchantScan>(),
            sp.GetRequiredService<IRecordEcho>(),
            sp.GetRequiredService<ILogger<CategorizationWorker>>()));

        services.AddHostedService(sp => new TranscriptionWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            options,
            CategorizationWorker.CreateWorkerId(),
            sp.GetRequiredService<IRecordEcho>(),
            sp.GetRequiredService<ILogger<TranscriptionWorker>>()));

        return services;
    }
}
