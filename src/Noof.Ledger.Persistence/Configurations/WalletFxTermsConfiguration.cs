using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class WalletFxTermsConfiguration : IEntityTypeConfiguration<WalletFxTerms>
{
    public void Configure(EntityTypeBuilder<WalletFxTerms> builder)
    {
        builder.ToTable("wallet_fx_terms", table => table.HasCheckConstraint("ck_wallet_fx_terms_rate_positive", "rate > 0"));

        builder.HasKey(t => new { t.WalletId, t.Currency });

        builder.Property(t => t.WalletId).HasColumnName("wallet_id");
        builder.Property(t => t.Currency)
            .HasColumnName("currency")
            .HasMaxLength(3)
            .IsRequired()
            .HasConversion(c => c.Value, v => new CurrencyCode(v));
        builder.Property(t => t.Rate).HasColumnName("rate").HasPrecision(24, 12);
        builder.Property(t => t.FeePercent).HasColumnName("fee_percent").HasPrecision(9, 4);
        builder.Property(t => t.FeeFixed).HasColumnName("fee_fixed").HasPrecision(19, 4);
        builder.Property(t => t.FeeMinimum).HasColumnName("fee_minimum").HasPrecision(19, 4);

        builder.HasOne<Wallet>()
            .WithMany()
            .HasForeignKey(t => t.WalletId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
