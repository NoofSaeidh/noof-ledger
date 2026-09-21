using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence;

public class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Merchant> Merchants => Set<Merchant>();
    public DbSet<MerchantAlias> MerchantAliases => Set<MerchantAlias>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<LineItem> LineItems => Set<LineItem>();
    public DbSet<CategorizationJob> CategorizationJobs => Set<CategorizationJob>();

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
