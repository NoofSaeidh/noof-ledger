using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class ReceiptLineConfiguration : IEntityTypeConfiguration<ReceiptLine>
{
    public void Configure(EntityTypeBuilder<ReceiptLine> builder)
    {
        builder.ToTable("receipt_lines");

        builder.HasKey(l => l.Id);

        builder.Property(l => l.Id).HasColumnName("id");
        builder.Property(l => l.ReceiptId).HasColumnName("receipt_id");
        builder.Property(l => l.Ordinal).HasColumnName("ordinal");
        builder.Property(l => l.Name).HasColumnName("name").HasMaxLength(512).IsRequired();
        builder.Property(l => l.Quantity).HasColumnName("quantity").HasPrecision(19, 4);
        builder.Property(l => l.Unit).HasColumnName("unit").HasMaxLength(32);
        builder.Property(l => l.UnitPrice).HasColumnName("unit_price").HasPrecision(19, 4);
        builder.Property(l => l.Total).HasColumnName("total").HasPrecision(19, 4);
        builder.Property(l => l.TaxLabel).HasColumnName("tax_label").HasMaxLength(32);

        builder.HasIndex(l => new { l.ReceiptId, l.Ordinal }).IsUnique();

        builder.HasOne<Receipt>()
            .WithMany()
            .HasForeignKey(l => l.ReceiptId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
