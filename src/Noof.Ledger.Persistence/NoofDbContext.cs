using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence;

public class NoofDbContext(DbContextOptions<NoofDbContext> options) : DbContext(options)
{
    public DbSet<MoneyProbeEntity> MoneyProbes => Set<MoneyProbeEntity>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<decimal>().HavePrecision(19, 4);
        builder.Properties<DateTimeOffset>().HaveColumnType("timestamptz");
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("public");

        builder.Entity<MoneyProbeEntity>(entity =>
        {
            entity.ToTable("money_probe_entities");
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.RecordedAt).HasColumnName("recorded_at");

            entity.ComplexProperty(e => e.Amount, money =>
            {
                money.Property(m => m.Amount).HasColumnName("amount").HasPrecision(19, 4);
                money.Property(m => m.Currency)
                     .HasColumnName("currency")
                     .HasMaxLength(3)
                     .IsRequired()
                     .HasConversion(c => c.Value, v => new CurrencyCode(v));
            });
        });
    }
}
