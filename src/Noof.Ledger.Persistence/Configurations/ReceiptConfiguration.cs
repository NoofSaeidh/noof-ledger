using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class ReceiptConfiguration : IEntityTypeConfiguration<Receipt>
{
    // The duplicate detector (R-2, spec §1): a seller and fiscal number the ledger has already
    // recorded is the same receipt, whichever channel it arrives through the second time.
    internal const string DuplicateIndex = "ix_receipts_seller_tax_id_fiscal_number";

    // The same guard for an exchange slip, keyed by the office's PIB and the slip's own number; 6 is ReceiptKind.Exchange.
    internal const string SlipDuplicateIndex = "ix_receipts_seller_tax_id_slip_number";

    public void Configure(EntityTypeBuilder<Receipt> builder)
    {
        builder.ToTable("receipts");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Id).HasColumnName("id");
        builder.Property(r => r.TransactionId).HasColumnName("transaction_id");
        builder.Property(r => r.Source).HasColumnName("source");
        builder.Property(r => r.VerificationUrl).HasColumnName("verification_url");
        builder.Property(r => r.SellerTaxId).HasColumnName("seller_tax_id").HasMaxLength(32);
        builder.Property(r => r.SellerName).HasColumnName("seller_name").HasMaxLength(256);
        builder.Property(r => r.SellerAddress).HasColumnName("seller_address");
        builder.Property(r => r.LocationName).HasColumnName("location_name").HasMaxLength(256);
        builder.Property(r => r.FiscalNumber).HasColumnName("fiscal_number").HasMaxLength(64);
        builder.Property(r => r.SlipNumber).HasColumnName("slip_number");
        builder.Property(r => r.IssuedAt).HasColumnName("issued_at");
        builder.Property(r => r.Kind).HasColumnName("receipt_kind");
        builder.Property(r => r.PaymentMethod).HasColumnName("payment_method");
        builder.Property(r => r.QrTotal).HasColumnName("qr_total").HasPrecision(19, 4);
        builder.Property(r => r.TelegramFileId).HasColumnName("telegram_file_id");
        builder.Property(r => r.CreatedAt).HasColumnName("created_at");

        builder.ComplexProperty(r => r.Total, money =>
        {
            money.Property(m => m.Amount).HasColumnName("total").HasPrecision(19, 4);
            money.Property(m => m.Currency)
                 .HasColumnName("currency")
                 .HasMaxLength(3)
                 .IsRequired()
                 .HasConversion(c => c.Value, v => new CurrencyCode(v));
        });

        builder.HasIndex(r => new { r.SellerTaxId, r.FiscalNumber })
            .IsUnique()
            .HasFilter("seller_tax_id IS NOT NULL AND fiscal_number IS NOT NULL")
            .HasDatabaseName(DuplicateIndex);

        builder.HasIndex(r => new { r.SellerTaxId, r.SlipNumber })
            .IsUnique()
            .HasFilter("receipt_kind = 6 AND seller_tax_id IS NOT NULL AND slip_number IS NOT NULL")
            .HasDatabaseName(SlipDuplicateIndex);

        builder.HasOne<Transaction>()
            .WithOne()
            .HasForeignKey<Receipt>(r => r.TransactionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
