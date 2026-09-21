using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class WalletConfiguration : IEntityTypeConfiguration<Wallet>
{
    public void Configure(EntityTypeBuilder<Wallet> builder)
    {
        builder.ToTable("wallets");

        builder.HasKey(w => w.Id);

        builder.Property(w => w.Id).HasColumnName("id");
        builder.Property(w => w.Name).HasColumnName("name").HasMaxLength(128).IsRequired();

        builder.Property(w => w.Currency)
            .HasColumnName("currency")
            .HasMaxLength(3)
            .IsRequired()
            .HasConversion(c => c.Value, v => new CurrencyCode(v));

        builder.Property(w => w.IsDefault).HasColumnName("is_default").IsRequired();

        builder.HasData(new Wallet
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Name = "Main Wallet",
            Currency = CurrencyCode.Rsd,
            IsDefault = true,
        });
    }
}
