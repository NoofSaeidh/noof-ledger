using AwesomeAssertions;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Tests;

// ExtractedExchange.Assess is pure Application code, tested here beside ProposalMapperTests because
// Application has no test project of its own. No database, so no [Collection].
public class ExtractedExchangeAssessTests
{
    const string Pib = "123456789";

    static ExtractedExchange Slip(
        decimal? given = 100.00m, string? givenCurrency = "EUR", decimal? received = 11700.00m, string? receivedCurrency = "RSD",
        decimal? rate = 117.0000m, decimal? commission = null, string? commissionCurrency = null,
        string? slipNumber = "PZ-2026-0917") =>
        new(given, givenCurrency, received, receivedCurrency, rate, commission, commissionCurrency, slipNumber);

    static SlipAssessment Assess(ExtractedExchange slip, string? sellerTaxId = Pib, bool taxIdMalformed = false) =>
        slip.Assess(sellerTaxId, taxIdMalformed);

    [Fact]
    public void A_sale_of_euros_that_agrees_with_its_printed_rate_is_recorded()
    {
        var assessment = Assess(Slip());

        assessment.Disposition.Should().Be(SlipDisposition.Record);
        assessment.Problems.Should().BeEmpty();
        assessment.Missing.Should().BeEmpty();
    }

    [Fact]
    public void A_purchase_of_euros_agrees_with_the_rate_from_the_other_side()
    {
        Assess(Slip(given: 11750.00m, givenCurrency: "RSD", received: 100.00m, receivedCurrency: "EUR", rate: 117.5000m))
            .Disposition.Should().Be(SlipDisposition.Record);
    }

    // 1000 EUR at 117.1235 is 117123.50 RSD; the tolerance is 0.01 + 1000 × 0.00005 = 0.06.
    public static TheoryData<decimal, SlipDisposition> ThousandEurosAtAFourDecimalRate => new()
    {
        { 117123.44m, SlipDisposition.Record },
        { 117123.56m, SlipDisposition.Record },
        { 117123.43m, SlipDisposition.Hold },
        { 117123.57m, SlipDisposition.Hold },
    };

    [Theory]
    [MemberData(nameof(ThousandEurosAtAFourDecimalRate))]
    public void The_dinar_side_may_differ_by_one_para_plus_what_four_printed_decimals_can_carry(decimal dinars, SlipDisposition expected)
    {
        var assessment = Assess(Slip(given: 1000.00m, received: dinars, rate: 117.1235m));

        assessment.Disposition.Should().Be(expected);
        assessment.Problems.Contains(SlipProblem.AmountsDisagree).Should().Be(expected == SlipDisposition.Hold);
    }

    // 10 EUR at 117 is 1170.00 RSD; the tolerance is 0.01 + 10 × 0.00005 = 0.0105.
    public static TheoryData<decimal, SlipDisposition> TenEurosAtAWholeRate => new()
    {
        { 1170.01m, SlipDisposition.Record },
        { 1170.02m, SlipDisposition.Hold },
    };

    [Theory]
    [MemberData(nameof(TenEurosAtAWholeRate))]
    public void A_small_exchange_is_held_beyond_one_para(decimal dinars, SlipDisposition expected) =>
        Assess(Slip(given: 10.00m, received: dinars, rate: 117.0000m)).Disposition.Should().Be(expected);

    [Fact]
    public void A_commission_in_dinars_taken_on_a_purchase_is_allowed_for()
    {
        Assess(Slip(given: 11850.00m, givenCurrency: "RSD", received: 100.00m, receivedCurrency: "EUR", rate: 117.0000m,
                commission: 150.00m, commissionCurrency: "RSD"))
            .Disposition.Should().Be(SlipDisposition.Record);
    }

    [Fact]
    public void A_commission_in_the_foreign_currency_is_allowed_for_at_the_printed_rate()
    {
        Assess(Slip(given: 100.00m, received: 11583.00m, commission: 1.00m, commissionCurrency: "EUR"))
            .Disposition.Should().Be(SlipDisposition.Record);
    }

    [Fact]
    public void A_commission_printed_with_no_currency_is_read_as_dinars()
    {
        Assess(Slip(given: 100.00m, received: 11550.00m, commission: 150.00m, commissionCurrency: null))
            .Disposition.Should().Be(SlipDisposition.Record);
    }

    [Fact]
    public void A_commission_in_a_third_currency_cannot_be_allowed_for_so_the_slip_is_held()
    {
        Assess(Slip(given: 100.00m, received: 11550.00m, commission: 1.30m, commissionCurrency: "USD"))
            .Problems.Should().Equal(SlipProblem.AmountsDisagree);
    }

    [Fact]
    public void Without_a_printed_rate_there_is_nothing_to_check_the_amounts_against()
    {
        Assess(Slip(received: 11000.00m, rate: null)).Disposition.Should().Be(SlipDisposition.Record);
    }

    [Fact]
    public void A_rate_between_two_foreign_currencies_is_never_checked_against()
    {
        Assess(Slip(givenCurrency: "EUR", received: 120.00m, receivedCurrency: "USD", rate: 1.0800m))
            .Problems.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("12345678", false)]
    [InlineData("1234567890", false)]
    [InlineData("12345678A", false)]
    [InlineData("123456789", true)]
    public void A_PIB_that_is_unread_malformed_or_not_nine_digits_holds_the_slip(string? sellerTaxId, bool malformed)
    {
        var assessment = Assess(Slip(), sellerTaxId, malformed);

        assessment.Disposition.Should().Be(SlipDisposition.Hold);
        assessment.Problems.Should().Equal(SlipProblem.TaxIdUnreadable);
    }

    [Fact]
    public void A_nine_digit_PIB_with_surrounding_spaces_is_read()
    {
        Assess(Slip(), " 123456789 ").Problems.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void An_unread_slip_number_holds_the_slip_because_a_repeat_could_not_be_caught(string? slipNumber)
    {
        var assessment = Assess(Slip(slipNumber: slipNumber));

        assessment.Disposition.Should().Be(SlipDisposition.Hold);
        assessment.Problems.Should().Equal(SlipProblem.SlipNumberUnreadable);
    }

    [Fact]
    public void Every_problem_is_named_in_one_order()
    {
        Assess(Slip(received: 11650.00m, slipNumber: null), sellerTaxId: null).Problems
            .Should().Equal(SlipProblem.AmountsDisagree, SlipProblem.TaxIdUnreadable, SlipProblem.SlipNumberUnreadable);
    }

    [Fact]
    public void A_received_amount_the_rate_can_fill_is_not_missing()
    {
        var assessment = Assess(Slip(received: null));

        assessment.Disposition.Should().Be(SlipDisposition.Record);
        assessment.Missing.Should().BeEmpty();
    }

    [Fact]
    public void A_received_amount_with_no_rate_to_fill_it_makes_the_slip_incomplete()
    {
        var assessment = Assess(Slip(received: null, rate: null));

        assessment.Disposition.Should().Be(SlipDisposition.Incomplete);
        assessment.Missing.Should().Equal(SlipMissing.ReceivedAmount);
    }

    [Fact]
    public void A_rate_between_two_foreign_currencies_cannot_fill_the_received_amount()
    {
        Assess(Slip(givenCurrency: "EUR", received: null, receivedCurrency: "USD", rate: 1.0800m))
            .Missing.Should().Equal(SlipMissing.ReceivedAmount);
    }

    [Fact]
    public void Every_unread_amount_and_currency_is_listed_in_one_order()
    {
        var assessment = Assess(Slip(given: null, givenCurrency: null, received: null, receivedCurrency: null));

        assessment.Disposition.Should().Be(SlipDisposition.Incomplete);
        assessment.Missing.Should().Equal(
            SlipMissing.GivenAmount, SlipMissing.GivenCurrency, SlipMissing.ReceivedAmount, SlipMissing.ReceivedCurrency);
    }

    [Theory]
    [InlineData("CHF")]
    [InlineData("eu")]
    public void A_currency_the_ledger_does_not_hold_counts_as_unread(string givenCurrency)
    {
        Assess(Slip(givenCurrency: givenCurrency)).Missing.Should().Equal(SlipMissing.GivenCurrency);
    }

    [Fact]
    public void A_lower_case_currency_is_read()
    {
        Assess(Slip(givenCurrency: "eur", receivedCurrency: " rsd ")).Disposition.Should().Be(SlipDisposition.Record);
    }

    [Fact]
    public void A_zero_amount_counts_as_unread()
    {
        Assess(Slip(given: 0m)).Missing.Should().Equal(SlipMissing.GivenAmount);
    }

    // The rate can fill a received amount that is not positive, so such a slip is recorded exactly as one whose
    // received amount went unread - never held for amounts that disagree with a figure nobody read.
    [Theory]
    [InlineData(0)]
    [InlineData(-11700)]
    public void A_received_amount_that_is_not_positive_is_assessed_as_unread(decimal received)
    {
        var assessment = Assess(Slip(received: received));

        assessment.Should().BeEquivalentTo(Assess(Slip(received: null)));
        assessment.Disposition.Should().Be(SlipDisposition.Record);
    }

    [Fact]
    public void A_given_amount_that_is_not_positive_names_no_disagreement()
    {
        Assess(Slip(given: 0m)).Problems.Should().Equal(Assess(Slip(given: null)).Problems);
    }

    [Fact]
    public void An_incomplete_slip_still_names_its_problems()
    {
        var assessment = Assess(Slip(received: null, rate: null, slipNumber: null));

        assessment.Disposition.Should().Be(SlipDisposition.Incomplete);
        assessment.Problems.Should().Equal(SlipProblem.SlipNumberUnreadable);
    }

    [Fact]
    public void The_printed_rate_prices_the_foreign_side_in_dinars_whichever_side_was_given()
    {
        Slip().PrintedRate().Should().Be(new ExchangeRate(CurrencyCode.Eur, 117.0000m, CurrencyCode.Rsd));
        Slip(given: 11750.00m, givenCurrency: "RSD", received: 100.00m, receivedCurrency: "eur", rate: 117.5000m).PrintedRate()
            .Should().Be(new ExchangeRate(CurrencyCode.Eur, 117.5000m, CurrencyCode.Rsd));
    }

    [Theory]
    [InlineData("EUR", "USD", 1.0800)]
    [InlineData("RSD", "RSD", 1.0000)]
    [InlineData("EUR", "RSD", 0.0000)]
    [InlineData("EUR", "CHF", 117.0000)]
    public void There_is_no_printed_rate_unless_exactly_one_side_is_dinars_and_the_rate_is_positive(
        string givenCurrency, string receivedCurrency, decimal rate)
    {
        Slip(givenCurrency: givenCurrency, receivedCurrency: receivedCurrency, rate: rate).PrintedRate().Should().BeNull();
    }

    [Fact]
    public void An_unread_rate_is_no_printed_rate()
    {
        Slip(rate: null).PrintedRate().Should().BeNull();
    }

    [Theory]
    [InlineData("EUR", "EUR")]
    [InlineData(" usd ", "USD")]
    [InlineData("CHF", null)]
    [InlineData("eu", null)]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void A_supported_currency_is_read_ignoring_case_and_surrounding_spaces(string? code, string? expected)
    {
        ExtractedExchange.SupportedCurrency(code).Should().Be(expected is null ? null : new CurrencyCode(expected));
    }

    [Theory]
    [InlineData(null, "RSD")]
    [InlineData("  ", "RSD")]
    [InlineData("eur", "EUR")]
    [InlineData("RSD", "RSD")]
    [InlineData("CHF", null)]
    public void CommissionCurrencyOrDinars_reads_the_printed_currency_or_dinars(string? commissionCurrency, string? expected)
    {
        Slip(commission: 150.00m, commissionCurrency: commissionCurrency).CommissionCurrencyOrDinars()
            .Should().Be(expected is null ? null : new CurrencyCode(expected));
    }
}
