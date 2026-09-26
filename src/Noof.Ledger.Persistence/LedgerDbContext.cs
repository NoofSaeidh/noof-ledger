using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Backup;
using Noof.Ledger.Persistence.Diagnostics;
using Noof.Ledger.Persistence.Revisions;
using Noof.Ledger.Persistence.Secrets;
using Noof.Ledger.Persistence.Settings;

namespace Noof.Ledger.Persistence;

internal sealed class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Merchant> Merchants => Set<Merchant>();
    public DbSet<MerchantAlias> MerchantAliases => Set<MerchantAlias>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<LineItem> LineItems => Set<LineItem>();
    public DbSet<Entry> Entries => Set<Entry>();
    public DbSet<BalanceCheck> BalanceChecks => Set<BalanceCheck>();
    public DbSet<CategorizationJob> CategorizationJobs => Set<CategorizationJob>();
    public DbSet<TransactionRevision> TransactionRevisions => Set<TransactionRevision>();
    public DbSet<AppSecret> Secrets => Set<AppSecret>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<BackupRun> BackupRuns => Set<BackupRun>();
    public DbSet<AppLogEntry> AppLogs => Set<AppLogEntry>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<decimal>().HavePrecision(19, 4);
        builder.Properties<DateTimeOffset>().HaveColumnType("timestamptz");
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("public");
        builder.ApplyConfigurationsFromAssembly(typeof(LedgerDbContext).Assembly);
    }
}
