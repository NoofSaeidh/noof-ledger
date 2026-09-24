using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class BalanceCheckConfiguration : IEntityTypeConfiguration<BalanceCheck>
{
    public void Configure(EntityTypeBuilder<BalanceCheck> builder)
    {
        builder.ToTable("balance_checks");

        builder.HasKey(b => b.TransactionId);

        builder.Property(b => b.TransactionId).HasColumnName("transaction_id");
        builder.Property(b => b.WalletId).HasColumnName("wallet_id");
        builder.Property(b => b.ComputedBefore).HasColumnName("computed_before");

        builder.ComplexProperty(b => b.Stated, money =>
        {
            money.Property(m => m.Amount).HasColumnName("stated_amount").HasPrecision(19, 4);
            money.Property(m => m.Currency)
                 .HasColumnName("currency")
                 .HasMaxLength(3)
                 .IsRequired()
                 .HasConversion(c => c.Value, v => new CurrencyCode(v));
        });

        builder.HasOne<Transaction>()
            .WithOne()
            .HasForeignKey<BalanceCheck>(b => b.TransactionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Wallet>()
            .WithMany()
            .HasForeignKey(b => b.WalletId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
