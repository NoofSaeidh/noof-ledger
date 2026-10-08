using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Fx;

// Source is in the key so a second rate source is a new row, never a migration (docs/decisions/q4-fx-rate-source.md).
// EUR is the base every rate is quoted against, so a row for it could only ever say 1.
internal sealed class FxRateRowConfiguration : IEntityTypeConfiguration<FxRateRow>
{
    public void Configure(EntityTypeBuilder<FxRateRow> builder)
    {
        builder.ToTable("fx_rates", table =>
        {
            table.HasCheckConstraint("ck_fx_rates_units_per_eur_positive", "units_per_eur > 0");
            table.HasCheckConstraint("ck_fx_rates_currency_not_eur", "currency <> 'EUR'");
        });

        builder.HasKey(rate => new { rate.Currency, rate.AsOfDate, rate.Source });

        builder.Property(rate => rate.Currency)
            .HasColumnName("currency")
            .HasMaxLength(3)
            .IsRequired()
            .HasConversion(currency => currency.Value, value => new CurrencyCode(value));
        builder.Property(rate => rate.AsOfDate).HasColumnName("as_of_date");
        builder.Property(rate => rate.Source).HasColumnName("source").IsRequired();
        builder.Property(rate => rate.UnitsPerEur).HasColumnName("units_per_eur").HasPrecision(24, 12);
        builder.Property(rate => rate.FetchedAt).HasColumnName("fetched_at");
    }
}
