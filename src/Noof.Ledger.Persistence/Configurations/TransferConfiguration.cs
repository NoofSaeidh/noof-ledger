using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class TransferConfiguration : IEntityTypeConfiguration<Transfer>
{
    // An instance rather than two lambdas: EF applies a converter for CurrencyCode to the CurrencyCode? column
    // and never hands it a null.
    static readonly ValueConverter<CurrencyCode, string> CurrencyCodeToString = new(c => c.Value, v => new CurrencyCode(v));

    public void Configure(EntityTypeBuilder<Transfer> builder)
    {
        builder.ToTable("transfers", table =>
        {
            table.HasCheckConstraint("ck_transfers_amounts_positive", "from_amount > 0 AND to_amount > 0");
            table.HasCheckConstraint("ck_transfers_wallets_differ", "from_wallet_id <> to_wallet_id");
            table.HasCheckConstraint("ck_transfers_stated_rate_has_base", "(stated_rate IS NULL) = (stated_rate_base IS NULL)");
        });

        builder.HasKey(t => t.TransactionId);

        builder.Property(t => t.TransactionId).HasColumnName("transaction_id");
        builder.Property(t => t.FromWalletId).HasColumnName("from_wallet_id");
        builder.Property(t => t.ToWalletId).HasColumnName("to_wallet_id");
        builder.Property(t => t.FeeLeg).HasColumnName("fee_leg");
        builder.Property(t => t.StatedRate).HasColumnName("stated_rate").HasPrecision(24, 12);
        builder.Property(t => t.StatedRateBase)
            .HasColumnName("stated_rate_base")
            .HasMaxLength(3)
            .HasConversion(CurrencyCodeToString);
        builder.Property(t => t.VenueMerchantId).HasColumnName("venue_merchant_id");

        builder.ComplexProperty(t => t.From, money =>
        {
            money.Property(m => m.Amount).HasColumnName("from_amount").HasPrecision(19, 4);
            money.Property(m => m.Currency)
                 .HasColumnName("from_currency")
                 .HasMaxLength(3)
                 .IsRequired()
                 .HasConversion(c => c.Value, v => new CurrencyCode(v));
        });

        builder.ComplexProperty(t => t.To, money =>
        {
            money.Property(m => m.Amount).HasColumnName("to_amount").HasPrecision(19, 4);
            money.Property(m => m.Currency)
                 .HasColumnName("to_currency")
                 .HasMaxLength(3)
                 .IsRequired()
                 .HasConversion(c => c.Value, v => new CurrencyCode(v));
        });

        builder.HasIndex(t => t.FromWalletId).HasDatabaseName("ix_transfers_from_wallet_id");
        builder.HasIndex(t => t.ToWalletId).HasDatabaseName("ix_transfers_to_wallet_id");

        builder.HasOne<Transaction>()
            .WithOne()
            .HasForeignKey<Transfer>(t => t.TransactionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Wallet>()
            .WithMany()
            .HasForeignKey(t => t.FromWalletId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Wallet>()
            .WithMany()
            .HasForeignKey(t => t.ToWalletId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Merchant>()
            .WithMany()
            .HasForeignKey(t => t.VenueMerchantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
