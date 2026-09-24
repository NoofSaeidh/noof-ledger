
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Workers;

internal static class WorkerRegistration
{
    public static IServiceCollection AddNoofWorkers(
        this IServiceCollection services, CategorizationWorkerOptions options, BackupWorkerOptions backupOptions)
    {
        services.AddSingleton(options);
        services.AddSingleton(backupOptions);
        services.AddHostedService(sp => new CategorizationWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            options,
            CategorizationWorker.CreateWorkerId(),
            sp.GetRequiredService<IProposalMapper>(),
            sp.GetRequiredService<IMerchantScan>(),
            sp.GetRequiredService<IRecordEcho>(),
            sp.GetRequiredService<IDatabaseGate>(),
            sp.GetRequiredService<ILogger<CategorizationWorker>>()));

        services.AddHostedService(sp => new TranscriptionWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            options,
            CategorizationWorker.CreateWorkerId(),
            sp.GetRequiredService<IRecordEcho>(),
            sp.GetRequiredService<IDatabaseGate>(),
            sp.GetRequiredService<ILogger<TranscriptionWorker>>()));

        if (backupOptions.Enabled)
        {
            services.AddHostedService(sp => new BackupWorker(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<TimeProvider>(),
                backupOptions,
                sp.GetRequiredService<IDatabaseGate>(),
                sp.GetRequiredService<ILogger<BackupWorker>>()));
        }

        return services;
    }
}
