using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class WalletConfiguration : IEntityTypeConfiguration<Wallet>
{
    internal const string OneDefaultPerCurrencyIndex = "ix_wallets_one_default_per_currency";

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

        builder.Property(w => w.Aliases).HasColumnName("aliases").HasDefaultValueSql("'{}'");
        builder.Property(w => w.IsDefaultForCurrency).HasColumnName("is_default_for_currency");
        builder.Property(w => w.Archived).HasColumnName("archived").HasDefaultValue(false);
        builder.Property(w => w.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");

        builder.HasIndex(w => w.Currency)
            .IsUnique()
            .HasFilter("is_default_for_currency")
            .HasDatabaseName(OneDefaultPerCurrencyIndex);
    }
}
