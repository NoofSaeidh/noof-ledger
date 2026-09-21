using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class MerchantAliasConfiguration : IEntityTypeConfiguration<MerchantAlias>
{
    public void Configure(EntityTypeBuilder<MerchantAlias> builder)
    {
        builder.ToTable("merchant_aliases");

        builder.HasKey(a => a.Folded);

        builder.Property(a => a.Folded).HasColumnName("folded").HasMaxLength(256);
        builder.Property(a => a.MerchantId).HasColumnName("merchant_id");
        builder.Property(a => a.CreatedAt).HasColumnName("created_at");

        builder.HasOne<Merchant>()
            .WithMany()
            .HasForeignKey(a => a.MerchantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
