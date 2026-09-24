using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class EntryConfiguration : IEntityTypeConfiguration<Entry>
{
    public void Configure(EntityTypeBuilder<Entry> builder)
    {
        builder.ToTable("entries");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.TransactionId).HasColumnName("transaction_id");
        builder.Property(e => e.WalletId).HasColumnName("wallet_id");
        builder.Property(e => e.Role).HasColumnName("role");

        builder.ComplexProperty(e => e.Amount, money =>
        {
            money.Property(m => m.Amount).HasColumnName("amount").HasPrecision(19, 4);
            money.Property(m => m.Currency)
                 .HasColumnName("currency")
                 .HasMaxLength(3)
                 .IsRequired()
                 .HasConversion(c => c.Value, v => new CurrencyCode(v));
        });

        builder.HasIndex(e => e.WalletId).HasDatabaseName("ix_entries_wallet_id");

        builder.HasOne<Transaction>()
            .WithMany()
            .HasForeignKey(e => e.TransactionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Wallet>()
            .WithMany()
            .HasForeignKey(e => e.WalletId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
