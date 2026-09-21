using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("categories");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Id).HasColumnName("id");
        builder.Property(c => c.ParentId).HasColumnName("parent_id");
        builder.Property(c => c.Slug).HasColumnName("slug").HasMaxLength(64).IsRequired();
        builder.Property(c => c.NameEn).HasColumnName("name_en").HasMaxLength(128).IsRequired();
        builder.Property(c => c.NameRu).HasColumnName("name_ru").HasMaxLength(128).IsRequired();
        builder.Property(c => c.IsActive).HasColumnName("is_active");

        builder.HasIndex(c => c.Slug).IsUnique();

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(c => c.ParentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
