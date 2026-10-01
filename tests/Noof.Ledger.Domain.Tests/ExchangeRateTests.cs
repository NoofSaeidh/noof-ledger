using AwesomeAssertions;
using Noof.Ledger.TestKit;

namespace Noof.Ledger.Domain.Tests;

public class ExchangeRateTests
{
    static readonly ExchangeRate EuroInDinars = new(CurrencyCode.Eur, 117m, CurrencyCode.Rsd);

    [Fact]
    public void Converting_the_base_currency_multiplies_by_the_rate()
    {
        EuroInDinars.Convert(new Money(100m, CurrencyCode.Eur)).Should().Be(new Money(11700m, CurrencyCode.Rsd));
    }

    [Fact]
    public void Converting_the_quote_currency_divides_by_the_rate_and_rounds_to_two_decimals()
    {
        EuroInDinars.Convert(new Money(11700m, CurrencyCode.Rsd)).Should().Be(new Money(100m, CurrencyCode.Eur));
        EuroInDinars.Convert(new Money(1000m, CurrencyCode.Rsd)).Should().Be(new Money(8.55m, CurrencyCode.Eur), "1000 / 117 = 8.547...");
    }

    [Fact]
    public void A_rate_written_the_other_way_round_converts_the_same_way()
    {
        var dinarInEuros = new ExchangeRate(CurrencyCode.Rsd, 0.0085m, CurrencyCode.Eur);

        dinarInEuros.Convert(new Money(100m, CurrencyCode.Eur)).Should().Be(new Money(11764.71m, CurrencyCode.Rsd), "100 / 0.0085 = 11764.705...");
        dinarInEuros.Convert(new Money(11700m, CurrencyCode.Rsd)).Should().Be(new Money(99.45m, CurrencyCode.Eur));
    }

    [Fact]
    public void A_converted_midpoint_rounds_away_from_zero()
    {
        new ExchangeRate(CurrencyCode.Eur, 117.005m, CurrencyCode.Rsd).Convert(new Money(1m, CurrencyCode.Eur))
            .Should().Be(new Money(117.01m, CurrencyCode.Rsd), "banker's rounding would give 117.00");
    }

    [Fact]
    public void Converting_a_currency_the_rate_does_not_name_throws()
    {
        var act = () => EuroInDinars.Convert(new Money(10m, CurrencyCode.Usd));

        act.Should().Throw<CurrencyMismatchException>().WithMessage("*USD*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-117)]
    public void A_rate_that_is_not_positive_converts_nothing(int quoteAmount)
    {
        var act = () => new ExchangeRate(CurrencyCode.Eur, quoteAmount, CurrencyCode.Rsd).Convert(new Money(100m, CurrencyCode.Eur));

        act.Should().ThrowExactly<InvalidOperationException>();
    }

    [Fact]
    public void The_realised_rate_is_based_on_the_dearer_currency_whichever_amount_comes_first()
    {
        ExchangeRate.Between(new Money(100m, CurrencyCode.Eur), new Money(11700m, CurrencyCode.Rsd))
            .Should().Be(new ExchangeRate(CurrencyCode.Eur, 117m, CurrencyCode.Rsd));
        ExchangeRate.Between(new Money(11700m, CurrencyCode.Rsd), new Money(100m, CurrencyCode.Eur))
            .Should().Be(new ExchangeRate(CurrencyCode.Eur, 117m, CurrencyCode.Rsd));
        ExchangeRate.Between(new Money(30m, CurrencyCode.Usd), new Money(15600m, CurrencyCode.Kzt))
            .Should().Be(new ExchangeRate(CurrencyCode.Usd, 520m, CurrencyCode.Kzt));
    }

    [Fact]
    public void The_realised_rate_is_not_rounded()
    {
        ExchangeRate.Between(new Money(3m, CurrencyCode.Eur), new Money(352m, CurrencyCode.Rsd))
            .QuoteAmount.Should().Be(352m / 3m);
    }

    [Fact]
    public void A_realised_rate_needs_two_currencies()
    {
        var act = () => ExchangeRate.Between(new Money(100m, CurrencyCode.Rsd), new Money(200m, CurrencyCode.Rsd));

        act.Should().ThrowExactly<ArgumentException>();
    }

    [Theory]
    [InlineData(0, 11700)]
    [InlineData(100, 0)]
    [InlineData(-100, 11700)]
    public void A_realised_rate_needs_two_positive_amounts(int euros, int dinars)
    {
        var act = () => ExchangeRate.Between(new Money(euros, CurrencyCode.Eur), new Money(dinars, CurrencyCode.Rsd));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Oriented_writes_the_rate_as_one_dearer_unit_in_cheaper_ones()
    {
        new ExchangeRate(CurrencyCode.Rsd, 0.0085m, CurrencyCode.Eur).Oriented()
            .Should().Be(new ExchangeRate(CurrencyCode.Eur, 1m / 0.0085m, CurrencyCode.Rsd));
        EuroInDinars.Oriented().Should().Be(EuroInDinars);
        new ExchangeRate(CurrencyCode.Eur, 1m, CurrencyCode.Usd).Oriented()
            .Should().Be(new ExchangeRate(CurrencyCode.Eur, 1m, CurrencyCode.Usd), "N >= 1 stays as written");
    }

    [Fact]
    public void A_rate_prints_oriented_with_four_decimals()
    {
        EuroInDinars.ToString().Should().Be("1 EUR = 117.0000 RSD");
        new ExchangeRate(CurrencyCode.Rsd, 1m / 117m, CurrencyCode.Eur).ToString().Should().Be("1 EUR = 117.0000 RSD");
        new ExchangeRate(CurrencyCode.Rsd, 0.0085m, CurrencyCode.Eur).ToString().Should().Be("1 EUR = 117.6471 RSD");
    }

    [Theory]
    [InlineData("ru-RU")]
    [InlineData("sr-Latn-RS")]
    public void A_rate_prints_the_same_under_any_culture(string culture)
    {
        using var scope = new CultureScope(culture);

        new ExchangeRate(CurrencyCode.Eur, 117.35m, CurrencyCode.Rsd).ToString().Should().Be("1 EUR = 117.3500 RSD");
    }
}
