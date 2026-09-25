using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class MerchantConfiguration : IEntityTypeConfiguration<Merchant>
{
    internal const string TaxIdIndex = "ix_merchants_tax_id";

    public void Configure(EntityTypeBuilder<Merchant> builder)
    {
        builder.ToTable("merchants");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.Id).HasColumnName("id");
        builder.Property(m => m.DisplayName).HasColumnName("display_name").HasMaxLength(256).IsRequired();
        builder.Property(m => m.Kind).HasColumnName("kind");
        builder.Property(m => m.TaxId).HasColumnName("tax_id").HasMaxLength(32);

        builder.HasIndex(m => m.TaxId)
            .IsUnique()
            .HasFilter("tax_id IS NOT NULL")
            .HasDatabaseName(TaxIdIndex);
    }
}
