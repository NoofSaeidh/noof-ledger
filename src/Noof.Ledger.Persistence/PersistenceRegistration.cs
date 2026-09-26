using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Application.Backup;
using Noof.Ledger.Application.Capture;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Editing;
using Noof.Ledger.Application.Jobs;
using Noof.Ledger.Application.Reporting;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Application.Transcription;
using Noof.Ledger.Application.Wallets;
using Noof.Ledger.Persistence.Auth;
using Noof.Ledger.Persistence.Backup;
using Noof.Ledger.Persistence.Balances;
using Noof.Ledger.Persistence.Capture;
using Noof.Ledger.Persistence.Categorization;
using Noof.Ledger.Persistence.Diagnostics;
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
        var connectionString = LedgerConnectionString.Resolve(configuration.GetConnectionString("Ledger"));

        services.AddDbContext<LedgerDbContext>(options => options
            .UseNpgsql(connectionString)
            .ConfigureWarnings(w => w.Log((RelationalEventId.CommandExecuted, LogLevel.Debug))));

        services.AddScoped<IUserStore, EfUserStore>();
        services.AddScoped<ISecretStore, EfSecretStore>();
        services.AddScoped<ICaptureStore, EfCaptureStore>();
        services.AddScoped<ICategorizationStore, EfCategorizationStore>();
        services.AddScoped<IRecordEditor, EfRecordEditor>();
        services.AddScoped<ITranscriptionStore, EfTranscriptionStore>();
        services.AddScoped<ICategoryCatalog, EfCategoryCatalog>();
        services.AddScoped<IMerchantDirectory, EfMerchantDirectory>();
        services.AddScoped<ISpendingReadModel, EfSpendingReadModel>();
        services.AddScoped<ITransactionList, EfTransactionList>();
        services.AddScoped<IBalanceReadModel, EfBalanceReadModel>();
        services.AddScoped<IWalletDirectory, EfWalletDirectory>();
        services.AddScoped<IWalletAdmin, EfWalletAdmin>();
        services.AddScoped<IJobQueue>(sp => new EfJobQueue(
            sp.GetRequiredService<LedgerDbContext>(),
            sp.GetRequiredService<TimeProvider>(),
            maxJobAttempts));
        services.AddScoped<IBackupLog, EfBackupLog>();
        services.AddScoped<IDatabaseLogLevelStore, EfDatabaseLogLevelStore>();
        services.AddScoped<IDatabaseDumper>(_ => new PgDumpDatabaseDumper(
            connectionString, configuration["Backup:PgDumpPath"] ?? PgDumpDatabaseDumper.DefaultPath));
        var retentionOptions = new LogRetentionOptions();
        configuration.GetSection("Logging:Retention").Bind(retentionOptions);
        services.AddSingleton(retentionOptions);

        services.AddScoped<ILogQuery, EfLogQuery>();
        services.AddScoped<ILogRetention, EfLogRetention>();
        services.AddScoped<ITransactionTrace, EfTransactionTrace>();

        services.AddHealthChecks().AddCheck<MigrationsHealthCheck>(HealthCheckNames.Migrations);

        return services;
    }

    public static async Task MigrateNoofDatabaseAsync(
        this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<LedgerDbContext>()
            .Database.MigrateAsync(cancellationToken);
    }

    // Opens the raw ADO.NET connection rather than Database.OpenConnectionAsync: that goes through
    // EF's execution strategy, which wraps a connection failure in InvalidOperationException as a
    // "consider enabling retry" hint - masking the NpgsqlException this startup probe needs to
    // classify (connection-refused vs. everything else).
    public static async Task OpenNoofDatabaseConnectionAsync(
        this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var connection = scope.ServiceProvider.GetRequiredService<LedgerDbContext>().Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        await connection.CloseAsync();
    }
}
