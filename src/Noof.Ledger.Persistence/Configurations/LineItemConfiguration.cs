using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class LineItemConfiguration : IEntityTypeConfiguration<LineItem>
{
    public void Configure(EntityTypeBuilder<LineItem> builder)
    {
        builder.ToTable("line_items");

        builder.HasKey(l => l.Id);

        builder.Property(l => l.Id).HasColumnName("id");
        builder.Property(l => l.TransactionId).HasColumnName("transaction_id");
        builder.Property(l => l.Description).HasColumnName("description").HasMaxLength(512).IsRequired();
        builder.Property(l => l.CategoryId).HasColumnName("category_id");
        builder.Property(l => l.CategorizedBy).HasColumnName("categorized_by");
        builder.Property(l => l.MerchantId).HasColumnName("merchant_id");

        builder.ComplexProperty(l => l.Amount, money =>
        {
            money.Property(m => m.Amount).HasColumnName("amount").HasPrecision(19, 4);
            money.Property(m => m.Currency)
                 .HasColumnName("currency")
                 .HasMaxLength(3)
                 .IsRequired()
                 .HasConversion(c => c.Value, v => new CurrencyCode(v));
        });

        builder.HasOne<Transaction>()
            .WithMany()
            .HasForeignKey(l => l.TransactionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(l => l.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Merchant>()
            .WithMany()
            .HasForeignKey(l => l.MerchantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
