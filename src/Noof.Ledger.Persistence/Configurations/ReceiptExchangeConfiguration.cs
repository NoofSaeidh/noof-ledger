using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class ReceiptExchangeConfiguration : IEntityTypeConfiguration<ReceiptExchange>
{
    public void Configure(EntityTypeBuilder<ReceiptExchange> builder)
    {
        builder.ToTable("receipt_exchanges");

        builder.HasKey(e => e.ReceiptId);

        builder.Property(e => e.ReceiptId).HasColumnName("receipt_id");
        builder.Property(e => e.GivenAmount).HasColumnName("given_amount").HasPrecision(19, 4);
        builder.Property(e => e.GivenCurrency).HasColumnName("given_currency").HasMaxLength(3);
        builder.Property(e => e.ReceivedAmount).HasColumnName("received_amount").HasPrecision(19, 4);
        builder.Property(e => e.ReceivedCurrency).HasColumnName("received_currency").HasMaxLength(3);
        builder.Property(e => e.Rate).HasColumnName("rate").HasPrecision(24, 12);
        builder.Property(e => e.CommissionAmount).HasColumnName("commission_amount").HasPrecision(19, 4);
        builder.Property(e => e.CommissionCurrency).HasColumnName("commission_currency").HasMaxLength(3);
        builder.Property(e => e.SlipNumber).HasColumnName("slip_number");

        builder.HasOne<Receipt>()
            .WithOne()
            .HasForeignKey<ReceiptExchange>(e => e.ReceiptId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
