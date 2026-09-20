using System.Globalization;
using AwesomeAssertions;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Domain.Tests;

public class CurrencyCodeTests
{
    [Fact]
    public void Implements_the_value_type_contract()
    {
        typeof(CurrencyCode).Should().Implement<IEquatable<CurrencyCode>>()
            .And.Implement<IComparable<CurrencyCode>>()
            .And.Implement<IComparable>();
    }

    [Fact]
    public void Orders_alphabetically()
    {
        CurrencyCode[] codes = [CurrencyCode.Usd, CurrencyCode.Eur, CurrencyCode.Rub, CurrencyCode.Rsd, CurrencyCode.Kzt];

        codes.Order().Should().Equal(CurrencyCode.Eur, CurrencyCode.Kzt, CurrencyCode.Rsd, CurrencyCode.Rub, CurrencyCode.Usd);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    [InlineData("sr-Latn-RS")]
    public void Orders_ordinally_regardless_of_culture(string culture)
    {
        CultureInfo.CurrentCulture = new CultureInfo(culture);

        // "LJ" is a single collating letter in Serbian Latin, sorting after "L".
        // Culture-sensitive comparison puts LZA before LJA; ordinal does the opposite.
        var lja = new CurrencyCode("LJA");
        var lza = new CurrencyCode("LZA");

        lja.CompareTo(lza).Should().BeNegative();
        (lja < lza).Should().BeTrue();
    }

    [Fact]
    public void Equality_is_ordinal_and_case_insensitive_through_normalisation()
    {
        new CurrencyCode("eur").Should().Be(CurrencyCode.Eur);
        new CurrencyCode("eur").GetHashCode().Should().Be(CurrencyCode.Eur.GetHashCode());
    }

    [Fact]
    public void Comparison_operators_agree_with_CompareTo()
    {
        (CurrencyCode.Eur < CurrencyCode.Usd).Should().BeTrue();
        (CurrencyCode.Usd > CurrencyCode.Eur).Should().BeTrue();
        (CurrencyCode.Eur <= new CurrencyCode("EUR")).Should().BeTrue();
        (CurrencyCode.Eur >= new CurrencyCode("EUR")).Should().BeTrue();
        (CurrencyCode.Usd < CurrencyCode.Eur).Should().BeFalse();
    }

    [Fact]
    public void Equal_codes_compare_as_equal()
    {
        CurrencyCode.Eur.CompareTo(new CurrencyCode("EUR")).Should().Be(0);
    }

    [Fact]
    public void A_currency_less_default_sorts_before_every_real_code()
    {
        default(CurrencyCode).CompareTo(CurrencyCode.Eur).Should().BeNegative();
        CurrencyCode.Eur.CompareTo(default).Should().BePositive();
        default(CurrencyCode).CompareTo(default).Should().Be(0);
    }

    [Fact]
    public void Sorting_never_throws_on_a_currency_less_default()
    {
        CurrencyCode[] codes = [CurrencyCode.Usd, default, CurrencyCode.Eur];

        var act = () => codes.Order().ToArray();

        act.Should().NotThrow();
    }

    [Fact]
    public void ToString_never_returns_null_even_for_a_currency_less_default()
    {
        default(CurrencyCode).ToString().Should().NotBeNull();
    }

    [Fact]
    public void Non_generic_CompareTo_rejects_a_foreign_type()
    {
        var act = () => ((IComparable)CurrencyCode.Eur).CompareTo("EUR");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Non_generic_CompareTo_treats_null_as_smallest()
    {
        ((IComparable)CurrencyCode.Eur).CompareTo(null).Should().BePositive();
    }
}
