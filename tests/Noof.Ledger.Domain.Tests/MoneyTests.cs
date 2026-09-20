using AwesomeAssertions;

namespace Noof.Ledger.Domain.Tests;

public class MoneyTests
{
    [Fact]
    public void Adding_same_currency_sums_the_amounts()
    {
        var sum = new Money(10.50m, CurrencyCode.Eur) + new Money(2.25m, CurrencyCode.Eur);

        sum.Should().Be(new Money(12.75m, CurrencyCode.Eur));
    }

    [Fact]
    public void Adding_different_currencies_throws()
    {
        var act = () => new Money(10m, CurrencyCode.Eur) + new Money(10m, CurrencyCode.Rsd);

        act.Should().Throw<CurrencyMismatchException>()
           .WithMessage("*EUR*RSD*");
    }

    [Fact]
    public void Adding_two_currency_less_defaults_throws()
    {
        var act = () => default(Money) + default(Money);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*currency*");
    }

    [Fact]
    public void Adding_a_valid_money_to_a_currency_less_default_throws()
    {
        var act = () => new Money(10m, CurrencyCode.Eur) + default(Money);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*currency*");
    }

    [Fact]
    public void Subtracting_two_currency_less_defaults_throws()
    {
        var act = () => default(Money) - default(Money);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*currency*");
    }

    [Fact]
    public void Subtracting_a_currency_less_default_from_a_valid_money_throws()
    {
        var act = () => new Money(10m, CurrencyCode.Eur) - default(Money);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*currency*");
    }

    [Fact]
    public void Subtracting_same_currency_subtracts_the_amounts()
    {
        var difference = new Money(10.50m, CurrencyCode.Eur) - new Money(2.25m, CurrencyCode.Eur);

        difference.Should().Be(new Money(8.25m, CurrencyCode.Eur));
    }

    [Fact]
    public void Subtracting_different_currencies_throws()
    {
        var act = () => new Money(10m, CurrencyCode.Eur) - new Money(10m, CurrencyCode.Rsd);

        act.Should().Throw<CurrencyMismatchException>()
           .WithMessage("*EUR*RSD*");
    }

    [Fact]
    public void Negating_flips_the_sign_and_keeps_the_currency()
    {
        (-new Money(10.50m, CurrencyCode.Eur)).Should().Be(new Money(-10.50m, CurrencyCode.Eur));
        (-new Money(-10.50m, CurrencyCode.Eur)).Should().Be(new Money(10.50m, CurrencyCode.Eur));
    }

    [Fact]
    public void Negating_a_currency_less_default_throws()
    {
        var act = () => -default(Money);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*currency*");
    }

    [Fact]
    public void Currency_code_rejects_anything_that_is_not_three_letters()
    {
        var act = () => new CurrencyCode("EURO");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Currency_code_is_upper_cased()
    {
        new CurrencyCode("eur").Value.Should().Be("EUR");
    }
}
