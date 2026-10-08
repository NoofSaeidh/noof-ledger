using AwesomeAssertions;

namespace Noof.Ledger.Domain.Tests;

public class MoneyAllocateTests
{
    static Money Kzt(decimal amount) => new(amount, CurrencyCode.Kzt);

    [Fact]
    public void Splits_in_proportion_and_the_largest_part_takes_the_rounding_remainder()
    {
        // 10.00 × 1/3 = 3.333… → 3.33; the larger part takes the rest: 10.00 - 3.33 = 6.67.
        Kzt(10.00m).Allocate([1m, 2m]).Should().Equal(Kzt(3.33m), Kzt(6.67m));
    }

    [Fact]
    public void A_charge_split_across_three_lines_adds_up_to_the_cent()
    {
        // 15601.00 over 20:7:3 (sum 30): 15601 × 7/30 = 3640.2333 → 3640.23; 15601 × 3/30 = 1560.10; the 20 takes
        // 15601.00 - 3640.23 - 1560.10 = 10400.67 (15601 × 20/30 = 10400.6667, so the remainder lands where it belongs).
        var parts = Kzt(15601.00m).Allocate([20m, 7m, 3m]);

        parts.Should().Equal(Kzt(10400.67m), Kzt(3640.23m), Kzt(1560.10m));
        parts.Sum(part => part.Amount).Should().Be(15601.00m);
    }

    [Fact]
    public void On_a_tie_the_first_of_the_largest_takes_the_remainder()
    {
        // 100.00 × 1/3 = 33.333… → 33.33 twice; the first takes 100.00 - 66.66 = 33.34.
        Kzt(100.00m).Allocate([1m, 1m, 1m]).Should().Equal(Kzt(33.34m), Kzt(33.33m), Kzt(33.33m));
    }

    [Fact]
    public void Every_other_part_is_rounded_half_away_from_zero()
    {
        // 0.10 × 1/4 = 0.025 → 0.03 away from zero (banker's rounding would give 0.02); the 2 takes 0.10 - 0.06 = 0.04.
        Kzt(0.10m).Allocate([1m, 2m, 1m]).Should().Equal(Kzt(0.03m), Kzt(0.04m), Kzt(0.03m));
    }

    [Fact]
    public void An_exact_half_cent_share_rounds_away_from_zero_when_the_weight_ratio_does_not_terminate()
    {
        // 900.45 × 1/90 = 10.005 exactly → 10.01; 1/90 rounded to 28 digits first would give 10.00499… → 10.00.
        // The 89 takes 900.45 - 10.01 = 890.44.
        Kzt(900.45m).Allocate([1m, 89m]).Should().Equal(Kzt(10.01m), Kzt(890.44m));
    }

    [Fact]
    public void The_largest_weight_is_the_largest_value_not_the_largest_magnitude()
    {
        // 10.00 over 5, -10, 8 (sum 3): 16.666… → 16.67 and -33.333… → -33.33; the 8 takes
        // 10.00 - 16.67 + 33.33 = 26.66. Had the -10 taken the remainder it would be -33.34 and the 8 26.67.
        Kzt(10.00m).Allocate([5m, -10m, 8m]).Should().Equal(Kzt(16.67m), Kzt(-33.33m), Kzt(26.66m));
    }

    [Fact]
    public void A_negative_amount_splits_by_the_same_rule()
    {
        // -10.00 × 1/3 = -3.333… → -3.33; the larger part takes -10.00 - (-3.33) = -6.67.
        Kzt(-10.00m).Allocate([1m, 2m]).Should().Equal(Kzt(-3.33m), Kzt(-6.67m));
    }

    [Fact]
    public void A_zero_weight_gets_nothing_and_one_weight_gets_everything()
    {
        Kzt(9.99m).Allocate([0m, 3m]).Should().Equal(Kzt(0m), Kzt(9.99m));
        Kzt(12.34m).Allocate([5m]).Should().Equal(Kzt(12.34m));
    }

    [Fact]
    public void A_discount_line_takes_a_negative_share_of_the_charge()
    {
        // 2500.00 KZT over 30.00 and -5.00 USD (sum 25.00): the discount's share is 2500 × -5/25 = -500.00; the 30.00,
        // the largest, takes 2500.00 - (-500.00) = 3000.00.
        var parts = Kzt(2500.00m).Allocate([30.00m, -5.00m]);

        parts.Should().Equal(Kzt(3000.00m), Kzt(-500.00m));
        parts.Sum(part => part.Amount).Should().Be(2500.00m);
    }

    [Fact]
    public void Refuses_no_weights_and_weights_that_do_not_sum_above_zero()
    {
        var none = () => Kzt(10.00m).Allocate([]);
        var zero = () => Kzt(10.00m).Allocate([0m, 0m]);
        var cancelling = () => Kzt(10.00m).Allocate([5m, -5m]);
        var negative = () => Kzt(10.00m).Allocate([1m, -2m]);

        none.Should().Throw<ArgumentException>().WithParameterName("weights");
        zero.Should().Throw<ArgumentException>().WithParameterName("weights");
        cancelling.Should().Throw<ArgumentException>().WithParameterName("weights");
        negative.Should().Throw<ArgumentException>().WithParameterName("weights");
    }
}
