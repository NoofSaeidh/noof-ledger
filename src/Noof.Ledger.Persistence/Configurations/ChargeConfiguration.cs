using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class ChargeConfiguration : IEntityTypeConfiguration<Charge>
{
    public void Configure(EntityTypeBuilder<Charge> builder)
    {
        builder.ToTable("charges", table => table.HasCheckConstraint(
            "ck_charges_amounts", "charged_amount > 0 AND fee_amount >= 0 AND rate_used > 0"));

        builder.HasKey(c => new { c.TransactionId, c.Currency });

        builder.Property(c => c.TransactionId).HasColumnName("transaction_id");
        builder.Property(c => c.Currency)
            .HasColumnName("currency")
            .HasMaxLength(3)
            .IsRequired()
            .HasConversion(c => c.Value, v => new CurrencyCode(v));
        builder.Property(c => c.ChargedAmount).HasColumnName("charged_amount").HasPrecision(19, 4);
        builder.Property(c => c.FeeAmount).HasColumnName("fee_amount").HasPrecision(19, 4);
        builder.Property(c => c.RateUsed).HasColumnName("rate_used").HasPrecision(24, 12);
        builder.Property(c => c.FeePercent).HasColumnName("fee_percent").HasPrecision(9, 4);
        builder.Property(c => c.FeeFixed).HasColumnName("fee_fixed").HasPrecision(19, 4);
        builder.Property(c => c.FeeMinimum).HasColumnName("fee_minimum").HasPrecision(19, 4);
        builder.Property(c => c.Source).HasColumnName("source");

        builder.HasOne<Transaction>()
            .WithMany()
            .HasForeignKey(c => c.TransactionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
