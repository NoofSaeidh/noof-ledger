using AwesomeAssertions;
using Noof.Domain;

namespace Noof.Domain.Tests;

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
