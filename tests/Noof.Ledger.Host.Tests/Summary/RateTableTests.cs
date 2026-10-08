using AwesomeAssertions;
using Noof.Ledger.Application.Fx;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Host.Tests.Summary;

public class RateTableTests
{
    static DateOnly Oct(int day) => new(2026, 10, day);

    static readonly CurrencyCode Eur = CurrencyCode.Eur;
    static readonly CurrencyCode Rsd = CurrencyCode.Rsd;
    static readonly CurrencyCode Kzt = CurrencyCode.Kzt;
    static readonly CurrencyCode Usd = CurrencyCode.Usd;

    // RSD on the 3rd and the 7th, KZT on the 7th only, nothing for USD.
    static readonly RateTable Rates = new(
    [
        new FxRate(CurrencyCode.Rsd, new DateOnly(2026, 10, 3), 117.00m),
        new FxRate(CurrencyCode.Rsd, new DateOnly(2026, 10, 7), 117.20m),
        new FxRate(CurrencyCode.Kzt, new DateOnly(2026, 10, 7), 520.00m),
    ]);

    [Fact]
    public void The_same_currency_needs_no_rate()
    {
        new RateTable([]).TryConvert(12.34m, Rsd, Rsd, Oct(5)).Should().Be(new RateConversion(12.34m, false, null, null));
    }

    [Fact]
    public void EUR_to_another_currency_uses_that_days_rate()
    {
        // 10.00 EUR / 1 × 117.20
        Rates.TryConvert(10.00m, Eur, Rsd, Oct(7)).Should().Be(new RateConversion(1172.00m, false, Oct(7), Oct(7)));
    }

    [Fact]
    public void The_latest_rate_on_or_before_the_day_counts_and_three_days_old_is_still_exact()
    {
        // The 6th has no rate of its own; the 3rd's is three days old. 1170.00 / 117.00 × 1
        Rates.TryConvert(1170.00m, Rsd, Eur, Oct(6)).Should().Be(new RateConversion(10.00m, false, Oct(3), Oct(3)));
    }

    [Fact]
    public void A_rate_more_than_three_days_old_is_approximate()
    {
        var onlyThe3rd = new RateTable([new FxRate(Rsd, Oct(3), 117.00m)]);

        // Four days old on the 7th. 1170.00 / 117.00
        onlyThe3rd.TryConvert(1170.00m, Rsd, Eur, Oct(7)).Should().Be(new RateConversion(10.00m, true, Oct(3), Oct(3)));
    }

    [Fact]
    public void With_only_a_later_rate_that_one_is_used_and_marked_approximate()
    {
        // Nothing on or before the 1st, so the earliest after it: the 3rd's 117.00.
        Rates.TryConvert(1170.00m, Rsd, Eur, Oct(1)).Should().Be(new RateConversion(10.00m, true, Oct(3), Oct(3)));
    }

    [Fact]
    public void Between_two_other_currencies_both_rates_count_and_name_the_range()
    {
        var mixed = new RateTable([new FxRate(Rsd, Oct(3), 117.00m), new FxRate(Kzt, Oct(7), 520.00m)]);

        // 5200.00 KZT / 520.00 × 117.00 = 1170.00 RSD; the RSD rate is four days old on the 7th, so "≈".
        mixed.TryConvert(5200.00m, Kzt, Rsd, Oct(7)).Should().Be(new RateConversion(1170.00m, true, Oct(3), Oct(7)));
        // Both rates of the 7th: 5200.00 / 520.00 × 117.20
        Rates.TryConvert(5200.00m, Kzt, Rsd, Oct(7)).Should().Be(new RateConversion(1172.00m, false, Oct(7), Oct(7)));
    }

    [Fact]
    public void A_currency_without_any_rate_cannot_be_converted_either_way()
    {
        Rates.TryConvert(10.00m, Usd, Eur, Oct(7)).Should().BeNull();
        Rates.TryConvert(10.00m, Eur, Usd, Oct(7)).Should().BeNull();
        Rates.TryConvert(10.00m, Rsd, Usd, Oct(7)).Should().BeNull();
    }

    [Fact]
    public void A_table_without_any_rate_is_empty()
    {
        new RateTable([]).IsEmpty.Should().BeTrue();
        Rates.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void A_conversion_keeps_full_precision()
    {
        var conversion = Rates.TryConvert(100.00m, Rsd, Eur, Oct(7));

        // Rounding happens only when a figure is formatted (spec §1).
        conversion.Should().NotBeNull();
        conversion.Value.Amount.Should().Be(100.00m / 117.20m).And.NotBe(0.85m);
    }
}
