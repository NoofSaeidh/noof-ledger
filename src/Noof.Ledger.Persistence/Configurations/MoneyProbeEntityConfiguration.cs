using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class MoneyProbeEntityConfiguration : IEntityTypeConfiguration<MoneyProbeEntity>
{
    public void Configure(EntityTypeBuilder<MoneyProbeEntity> entity)
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
    }
}
