using AwesomeAssertions;

namespace Noof.Ledger.Domain.Tests;

public class MoneyAllocateTests
{
    static Money Kzt(decimal amount) => new(amount, CurrencyCode.Kzt);

    [Fact]
    public void Splits_in_proportion_and_the_left_over_cent_goes_to_the_largest_cut_off_fraction()
    {
        // 10.00 over 1, 2 (sum 3): exact 3.3333… and 6.6666…, cut to 3.33 and 6.66 = 9.99. The cent left over goes to the
        // larger fraction cut off (0.0066… over 0.0033…): 3.33 and 6.67.
        Kzt(10.00m).Allocate([1m, 2m]).Should().Equal(Kzt(3.33m), Kzt(6.67m));
    }

    [Fact]
    public void A_charge_split_across_three_lines_adds_up_to_the_cent()
    {
        // 15601.00 over 20:7:3 (sum 30): exact 10400.6666…, 3640.2333…, 1560.10, cut to 10400.66, 3640.23, 1560.10
        // = 15600.99. The cent goes to the largest fraction cut off, the 20's 0.0066….
        var parts = Kzt(15601.00m).Allocate([20m, 7m, 3m]);

        parts.Should().Equal(Kzt(10400.67m), Kzt(3640.23m), Kzt(1560.10m));
        parts.Sum(part => part.Amount).Should().Be(15601.00m);
    }

    [Fact]
    public void On_a_tie_the_first_of_the_largest_fractions_takes_the_cent()
    {
        // 100.00 over three equal weights: 33.3333… each, cut to 33.33 = 99.99; all three cut off 0.0033…, so the first
        // takes the cent.
        Kzt(100.00m).Allocate([1m, 1m, 1m]).Should().Equal(Kzt(33.34m), Kzt(33.33m), Kzt(33.33m));
    }

    [Fact]
    public void A_share_that_is_exact_in_cents_keeps_its_exact_value()
    {
        // 0.10 over 1, 2, 1 (sum 4): exact 0.025, 0.05, 0.025, cut to 0.02, 0.05, 0.02 = 0.09. The 0.05 cut off nothing;
        // the two 0.025s tie on 0.005 and the first takes the cent.
        Kzt(0.10m).Allocate([1m, 2m, 1m]).Should().Equal(Kzt(0.03m), Kzt(0.05m), Kzt(0.02m));
    }

    [Fact]
    public void Cents_too_few_to_go_round_never_turn_a_share_negative()
    {
        // 0.02 over four equal weights: 0.005 each, cut to 0.00; the two cents go to the first two of the tie.
        var parts = Kzt(0.02m).Allocate([1m, 1m, 1m, 1m]);

        parts.Should().Equal(Kzt(0.01m), Kzt(0.01m), Kzt(0.00m), Kzt(0.00m));
        parts.Should().OnlyContain(part => part.Amount >= 0);
    }

    [Fact]
    public void An_exact_half_cent_share_ties_when_the_weight_ratio_does_not_terminate()
    {
        // 900.45 over 1, 89 (sum 90): exact 10.005 and 890.445, cut to 10.00 and 890.44 = 900.44. Both cut off exactly
        // 0.005, so the first takes the cent. Had 1/90 been rounded to 28 digits first, 10.00499… would cut off less
        // than the 89's share and lose the cent to it.
        Kzt(900.45m).Allocate([1m, 89m]).Should().Equal(Kzt(10.01m), Kzt(890.44m));
    }

    [Fact]
    public void A_negative_weight_is_cut_toward_zero_like_any_other()
    {
        // 10.00 over 5, -10, 8 (sum 3): exact 16.6666…, -33.3333…, 26.6666…, cut to 16.66, -33.33, 26.66 = 9.99. The
        // cent needs a positive fraction: the 5 and the 8 tie on 0.0066…, the -10's -0.0033… does not qualify, and the
        // first of the tie, the 5, takes it.
        Kzt(10.00m).Allocate([5m, -10m, 8m]).Should().Equal(Kzt(16.67m), Kzt(-33.33m), Kzt(26.66m));
    }

    [Fact]
    public void A_negative_cent_left_over_goes_to_the_discount_with_the_largest_negative_fraction()
    {
        // 0.10 over 7, -2, -2 (sum 3): exact 0.2333…, -0.0666…, -0.0666…, cut to 0.23, -0.06, -0.06 = 0.11. The -0.01
        // left over goes to the most negative fraction cut off: the two discounts tie on -0.0066…, the first takes it.
        var parts = Kzt(0.10m).Allocate([7m, -2m, -2m]);

        parts.Should().Equal(Kzt(0.23m), Kzt(-0.07m), Kzt(-0.06m));
        parts.Sum(part => part.Amount).Should().Be(0.10m);
    }

    [Fact]
    public void A_negative_amount_splits_by_the_same_rule()
    {
        // -10.00 over 1, 2: exact -3.3333… and -6.6666…, cut to -3.33 and -6.66 = -9.99. The -0.01 left over goes to the
        // most negative fraction cut off, the 2's -0.0066….
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
        // 2500.00 KZT over 30.00 and -5.00 USD (sum 25.00): exact 3000.00 and -500.00, nothing left over.
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
