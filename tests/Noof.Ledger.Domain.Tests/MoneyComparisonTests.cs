using System.Globalization;
using AwesomeAssertions;

namespace Noof.Ledger.Domain.Tests;

public class MoneyComparisonTests
{
    [Fact]
    public void Implements_the_value_type_contract()
    {
        typeof(Money).Should().Implement<IEquatable<Money>>()
            .And.Implement<IComparable<Money>>()
            .And.Implement<IComparable>();
    }

    [Fact]
    public void Compares_by_amount_within_one_currency()
    {
        new Money(9.55m, CurrencyCode.Eur).CompareTo(new Money(10.00m, CurrencyCode.Eur)).Should().BeNegative();
        new Money(10.00m, CurrencyCode.Eur).CompareTo(new Money(9.55m, CurrencyCode.Eur)).Should().BePositive();
        new Money(10.00m, CurrencyCode.Eur).CompareTo(new Money(10.00m, CurrencyCode.Eur)).Should().Be(0);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    [InlineData("sr-Latn-RS")]
    public void Sorts_correctly_in_every_culture(string culture)
    {
        CultureInfo.CurrentCulture = new CultureInfo(culture);

        decimal[] amounts = [-45.25m, 0.10m, 2.05m, 9.55m, 10.00m, 100.00m, 999.99m, 1234.50m];
        var shuffled = amounts.Reverse().Select(a => new Money(a, CurrencyCode.Eur));

        shuffled.Order().Select(m => m.Amount).Should().Equal(amounts);
    }

    [Fact]
    public void Comparison_operators_agree_with_CompareTo()
    {
        var less = new Money(9.55m, CurrencyCode.Eur);
        var more = new Money(10.00m, CurrencyCode.Eur);

        (less < more).Should().BeTrue();
        (more > less).Should().BeTrue();
        (less <= new Money(9.55m, CurrencyCode.Eur)).Should().BeTrue();
        (less >= new Money(9.55m, CurrencyCode.Eur)).Should().BeTrue();
        (more < less).Should().BeFalse();
    }

    [Fact]
    public void Comparing_different_currencies_throws()
    {
        var act = () => new Money(10m, CurrencyCode.Eur).CompareTo(new Money(10m, CurrencyCode.Rsd));

        act.Should().Throw<CurrencyMismatchException>()
           .WithMessage("*EUR*RSD*");
    }

    [Fact]
    public void The_less_than_operator_also_throws_across_currencies()
    {
        var act = () => new Money(10m, CurrencyCode.Eur) < new Money(10m, CurrencyCode.Rsd);

        act.Should().Throw<CurrencyMismatchException>();
    }

    [Fact]
    public void Comparing_a_currency_less_default_throws()
    {
        var act = () => new Money(10m, CurrencyCode.Eur).CompareTo(default);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*currency*");
    }

    [Fact]
    public void Sorting_a_mixed_currency_list_directly_throws_rather_than_inventing_an_order()
    {
        Money[] mixed = [new(10m, CurrencyCode.Eur), new(10m, CurrencyCode.Rsd)];

        var act = () => mixed.Order().ToArray();

        // Sorting wraps any comparer failure in "Failed to compare two elements in the array",
        // so the currency mismatch is only visible as the inner exception.
        act.Should().Throw<InvalidOperationException>()
           .WithInnerException<CurrencyMismatchException>();
    }

    [Fact]
    public void Mixed_currencies_sort_when_the_caller_says_how()
    {
        Money[] mixed =
        [
            new(10m, CurrencyCode.Usd),
            new(5m, CurrencyCode.Eur),
            new(30m, CurrencyCode.Eur),
            new(1m, CurrencyCode.Usd),
        ];

        var ordered = mixed.OrderBy(m => m.Currency).ThenBy(m => m.Amount).ToArray();

        ordered.Should().Equal(
            new Money(5m, CurrencyCode.Eur),
            new Money(30m, CurrencyCode.Eur),
            new Money(1m, CurrencyCode.Usd),
            new Money(10m, CurrencyCode.Usd));
    }

    [Fact]
    public void Non_generic_CompareTo_rejects_a_foreign_type()
    {
        var act = () => ((IComparable)new Money(1m, CurrencyCode.Eur)).CompareTo(1m);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Non_generic_CompareTo_treats_null_as_smallest()
    {
        ((IComparable)new Money(1m, CurrencyCode.Eur)).CompareTo(null).Should().BePositive();
    }
}
