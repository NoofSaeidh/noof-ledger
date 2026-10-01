using AwesomeAssertions;

namespace Noof.Ledger.Domain.Tests;

public class ChargeTermsTests
{
    // Acceptance 4: Kaspi KZT's terms for USD are 520 and 1 %.
    static readonly ChargeTerms KaspiUsd = new(520m, new FeeTerms(1m, null, null));

    [Fact]
    public void A_charge_is_the_foreign_sum_at_the_wallet_rate_with_the_fee_by_the_terms()
    {
        KaspiUsd.ChargeFor(30m).Should().Be(new ComputedCharge(15600m, 156m, 520m));
    }

    [Fact]
    public void A_computed_charge_rounds_to_two_decimals_away_from_zero()
    {
        new ChargeTerms(1m, FeeTerms.None).ChargeFor(12.345m).Should().Be(new ComputedCharge(12.35m, 0m, 1m));
    }

    public static TheoryData<decimal> NotPositive => new() { 0m, -5m, 0.000009m };

    [Theory]
    [MemberData(nameof(NotPositive))]
    public void A_charge_that_is_not_positive_is_no_charge(decimal foreignSum)
    {
        KaspiUsd.ChargeFor(foreignSum).Should().BeNull("a currency whose charge is not positive stays an unconverted line (M10)");
    }

    [Fact]
    public void A_computed_charge_keeps_the_terms_rate_exactly_and_takes_the_fee_on_the_rounded_charge()
    {
        var charge = new ChargeTerms(117.123456789012345m, new FeeTerms(1.5m, null, null)).ChargeFor(100m);

        charge.Should().Be(new ComputedCharge(11712.35m, 175.69m, 117.123456789012345m),
            "RateUsed is the terms' rate unrounded (a correction rebuilds the snapshot from it), and the fee is "
            + "1.5 % of the rounded 11712.35 = 175.68525");
    }

    [Fact]
    public void A_stated_charge_keeps_the_figure_said_and_takes_the_fee_from_the_terms()
    {
        ChargeTerms.Stated(15400m, 30m, statedFee: null, feeIncluded: false, KaspiUsd.Fee)
            .Should().Be(new ComputedCharge(15400m, 154m, 513.333333333333m));
    }

    [Fact]
    public void A_stated_charge_needs_no_wallet_terms()
    {
        ChargeTerms.Stated(15400m, 30m, statedFee: null, feeIncluded: false, FeeTerms.None)
            .Should().Be(new ComputedCharge(15400m, 0m, 513.333333333333m));
    }

    [Fact]
    public void A_stated_fee_replaces_the_terms()
    {
        ChargeTerms.Stated(15400m, 30m, statedFee: 150m, feeIncluded: false, KaspiUsd.Fee)
            .Should().Be(new ComputedCharge(15400m, 150m, 513.333333333333m));
    }

    [Fact]
    public void A_stated_fee_included_in_the_figure_is_taken_out_of_the_charge()
    {
        ChargeTerms.Stated(15550m, 30m, statedFee: 150m, feeIncluded: true, KaspiUsd.Fee)
            .Should().Be(new ComputedCharge(15400m, 150m, 513.333333333333m));
    }

    // percent, fixed, minimum, stated, expected charge, expected fee, expected rate used (over 30 USD).
    public static TheoryData<decimal?, decimal?, decimal?, decimal, decimal, decimal, decimal> FeeInsideTheFigure => new()
    {
        { 1m, null, null, 15756m, 15600m, 156m, 520m },
        { 1m, 30m, null, 15786m, 15600m, 186m, 520m },
        { 1m, null, 200m, 10200m, 10000m, 200m, 333.333333333333m },
    };

    [Theory]
    [MemberData(nameof(FeeInsideTheFigure))]
    public void A_fee_said_to_be_included_but_not_said_is_the_terms_fee_on_what_is_left(
        decimal? percent, decimal? fixedFee, decimal? minimum, decimal stated, decimal charged, decimal fee, decimal rateUsed)
    {
        var terms = new FeeTerms(percent, fixedFee, minimum);

        ChargeTerms.Stated(stated, 30m, statedFee: null, feeIncluded: true, terms).Should().Be(new ComputedCharge(charged, fee, rateUsed));
        terms.FeeOn(charged).Should().Be(fee, "the fee inside the figure is the terms' fee on the charge that is left");
        (charged + fee).Should().Be(stated);
    }

    [Fact]
    public void A_stated_charge_over_no_foreign_sum_is_no_charge()
    {
        ChargeTerms.Stated(15400m, 0m, statedFee: null, feeIncluded: false, KaspiUsd.Fee).Should().BeNull();
    }

    [Fact]
    public void A_negative_stated_fee_is_no_charge()
    {
        ChargeTerms.Stated(15400m, 30m, statedFee: -150m, feeIncluded: false, KaspiUsd.Fee).Should().BeNull();
        ChargeTerms.Stated(15400m, 30m, statedFee: -150m, feeIncluded: true, KaspiUsd.Fee).Should().BeNull();
    }

    [Fact]
    public void A_stated_fee_that_leaves_nothing_charged_is_no_charge()
    {
        ChargeTerms.Stated(150m, 30m, statedFee: 150m, feeIncluded: true, KaspiUsd.Fee).Should().BeNull();
        ChargeTerms.Stated(100m, 30m, statedFee: 150m, feeIncluded: true, KaspiUsd.Fee).Should().BeNull();
    }
}
