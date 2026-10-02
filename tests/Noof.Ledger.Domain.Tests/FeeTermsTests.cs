using AwesomeAssertions;

namespace Noof.Ledger.Domain.Tests;

public class FeeTermsTests
{
    // percent, fixed, minimum, charged, expected fee: every present/absent combination of the three terms, and
    // for each one that has a minimum, a charge where the minimum wins and one where it does not.
    public static TheoryData<decimal?, decimal?, decimal?, decimal, decimal> EveryCombination => new()
    {
        { null, null, null, 15600m, 0m },
        { 1.5m, null, null, 15600m, 234m },
        { null, 30m, null, 15600m, 30m },
        { null, null, 200m, 15600m, 200m },
        { 1.5m, 30m, null, 15600m, 264m },
        { 1.5m, null, 200m, 15600m, 234m },
        { 1.5m, null, 200m, 10000m, 200m },
        { null, 30m, 200m, 15600m, 200m },
        { null, 250m, 200m, 15600m, 250m },
        { 1.5m, 30m, 200m, 15600m, 264m },
        { 1.5m, 30m, 200m, 1000m, 200m },
    };

    [Theory]
    [MemberData(nameof(EveryCombination))]
    public void The_fee_is_the_percentage_plus_the_fixed_amount_but_never_below_the_minimum(
        decimal? percent, decimal? fixedFee, decimal? minimum, decimal charged, decimal expected)
    {
        new FeeTerms(percent, fixedFee, minimum).FeeOn(charged).Should().Be(expected);
    }

    [Fact]
    public void None_has_no_terms_and_costs_nothing()
    {
        FeeTerms.None.Should().Be(new FeeTerms(null, null, null));
        FeeTerms.None.FeeOn(15600m).Should().Be(0m);
    }

    [Fact]
    public void A_fee_midpoint_rounds_away_from_zero()
    {
        new FeeTerms(1m, null, null).FeeOn(1250.50m).Should().Be(12.51m, "1 % of 1250.50 is 12.505; banker's rounding would give 12.50");
    }

    [Fact]
    public void Rounding_happens_once_after_the_whole_formula()
    {
        new FeeTerms(1.4m, 0.004m, null).FeeOn(1m).Should().Be(0.02m,
            "0.014 + 0.004 = 0.018 rounds to 0.02; rounding each term first would give 0.01 + 0.00");
    }
}
