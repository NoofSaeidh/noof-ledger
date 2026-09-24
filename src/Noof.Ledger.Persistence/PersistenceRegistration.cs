using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Application.Capture;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Editing;
using Noof.Ledger.Application.Jobs;
using Noof.Ledger.Application.Reporting;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Application.Transcription;
using Noof.Ledger.Application.Wallets;
using Noof.Ledger.Persistence.Auth;
using Noof.Ledger.Persistence.Balances;
using Noof.Ledger.Persistence.Capture;
using Noof.Ledger.Persistence.Categorization;
using Noof.Ledger.Persistence.Editing;
using Noof.Ledger.Persistence.Jobs;
using Noof.Ledger.Persistence.Reporting;
using Noof.Ledger.Persistence.Secrets;
using Noof.Ledger.Persistence.Transcription;
using Noof.Ledger.Persistence.Wallets;

namespace Noof.Ledger.Persistence;

[SuppressMessage("Maintainability", "CA1515",
    Justification = "The one public way into this assembly. Making it internal would leave every "
        + "store unreachable from the Host, which is the opposite of what Phase 1C set out to do.")]
public static class PersistenceRegistration
{
    // maxJobAttempts is a parameter rather than a configuration key of its own so that EfJobQueue's
    // server-side attempt limit and CategorizationWorker's "is this the last attempt" check can
    // never independently drift. The Host passes CategorizationWorkerOptions.MaxAttempts to both.
    public static IServiceCollection AddNoofPersistence(
        this IServiceCollection services, IConfiguration configuration, int maxJobAttempts)
    {
        services.AddDbContext<LedgerDbContext>(options =>
            options.UseNpgsql(LedgerConnectionString.Resolve(configuration.GetConnectionString("Ledger"))));

        services.AddScoped<IUserStore, EfUserStore>();
        services.AddScoped<ISecretStore, EfSecretStore>();
        services.AddScoped<ICaptureStore, EfCaptureStore>();
        services.AddScoped<ICategorizationStore, EfCategorizationStore>();
        services.AddScoped<IRecordEditor, EfRecordEditor>();
        services.AddScoped<ITranscriptionStore, EfTranscriptionStore>();
        services.AddScoped<ICategoryCatalog, EfCategoryCatalog>();
        services.AddScoped<IMerchantDirectory, EfMerchantDirectory>();
        services.AddScoped<ISpendingReadModel, EfSpendingReadModel>();
        services.AddScoped<IBalanceReadModel, EfBalanceReadModel>();
        services.AddScoped<IWalletDirectory, EfWalletDirectory>();
        services.AddScoped<IWalletAdmin, EfWalletAdmin>();
        services.AddScoped<IJobQueue>(sp => new EfJobQueue(
            sp.GetRequiredService<LedgerDbContext>(),
            sp.GetRequiredService<TimeProvider>(),
            maxJobAttempts));

        return services;
    }

    public static async Task MigrateNoofDatabaseAsync(
        this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<LedgerDbContext>()
            .Database.MigrateAsync(cancellationToken);
    }
}
