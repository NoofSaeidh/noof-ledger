
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Diagnostics.Integrity;
using Noof.Ledger.Application.Receipts;

namespace Noof.Ledger.Host.Workers;

internal static class WorkerRegistration
{
    public static IServiceCollection AddNoofWorkers(
        this IServiceCollection services, CategorizationWorkerOptions options, BackupWorkerOptions backupOptions,
        TimeZoneInfo captureTimeZone)
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
            captureTimeZone,
            sp.GetRequiredService<IDatabaseGate>(),
            sp.GetRequiredService<IOperationTimer>(),
            sp.GetRequiredService<IFiscalVerificationUrl>(),
            sp.GetRequiredService<ILogger<CategorizationWorker>>()));

        services.AddHostedService(sp => new ReceiptCategorizationWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            options,
            CategorizationWorker.CreateWorkerId(),
            sp.GetRequiredService<IRecordEcho>(),
            captureTimeZone,
            sp.GetRequiredService<IDatabaseGate>(),
            sp.GetRequiredService<IOperationTimer>(),
            sp.GetRequiredService<IFiscalVerificationUrl>(),
            sp.GetRequiredService<ILogger<ReceiptCategorizationWorker>>()));

        services.AddHostedService(sp => new TranscriptionWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            options,
            CategorizationWorker.CreateWorkerId(),
            sp.GetRequiredService<IRecordEcho>(),
            captureTimeZone,
            sp.GetRequiredService<IDatabaseGate>(),
            sp.GetRequiredService<IOperationTimer>(),
            sp.GetRequiredService<ILogger<TranscriptionWorker>>()));

        services.AddHostedService(sp => new ExtractReceiptWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            options,
            CategorizationWorker.CreateWorkerId(),
            sp.GetRequiredService<IRecordEcho>(),
            captureTimeZone,
            sp.GetRequiredService<IDatabaseGate>(),
            sp.GetRequiredService<IOperationTimer>(),
            sp.GetRequiredService<ILogger<ExtractReceiptWorker>>()));

        services.AddHostedService(sp => new RecordExchangeWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            options,
            CategorizationWorker.CreateWorkerId(),
            sp.GetRequiredService<IRecordEcho>(),
            sp.GetRequiredService<IDatabaseGate>(),
            sp.GetRequiredService<IOperationTimer>(),
            sp.GetRequiredService<ILogger<RecordExchangeWorker>>()));

        if (backupOptions.Enabled)
        {
            services.AddHostedService(sp => new BackupWorker(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<TimeProvider>(),
                backupOptions,
                sp.GetRequiredService<IDatabaseGate>(),
                sp.GetRequiredService<IOperationTimer>(),
                sp.GetRequiredService<ILogger<BackupWorker>>()));
        }

        services.AddHostedService(sp => new LogRetentionWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IDatabaseGate>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IOperationTimer>(),
            sp.GetRequiredService<ILogger<LogRetentionWorker>>()));

        services.AddHostedService(sp => new BugReportExplanationWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            options,
            sp.GetRequiredService<IFindingText>(),
            sp.GetRequiredService<IDatabaseGate>(),
            sp.GetRequiredService<IOperationTimer>(),
            sp.GetRequiredService<ILogger<BugReportExplanationWorker>>()));

        return services;
    }
}
