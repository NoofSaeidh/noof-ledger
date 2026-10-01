using System.Globalization;
using AwesomeAssertions;

namespace Noof.Ledger.Domain.Tests;

public class TransferRequestTests
{
    static readonly ExchangeRate EuroInDinars = new(CurrencyCode.Eur, 117m, CurrencyCode.Rsd);

    static Money Rsd(decimal amount) => new(amount, CurrencyCode.Rsd);

    static Money Eur(decimal amount) => new(amount, CurrencyCode.Eur);

    static SettledTransfer Settle(TransferRequest request) =>
        request.TrySettle(out var settled, out var failure)
            ? settled
            : throw new InvalidOperationException($"Expected the transfer to settle, but it failed with {failure}.");

    static RecordFailureReason FailureOf(TransferRequest request)
    {
        request.TrySettle(out var settled, out var failure).Should().BeFalse();
        settled.Should().BeNull();
        return failure;
    }

    [Fact]
    public void A_withdrawal_in_one_currency_moves_the_same_amount_out_and_in()
    {
        Settle(new(Rsd(10000m), CurrencyCode.Rsd, null, null, null))
            .Should().Be(new SettledTransfer(Rsd(10000m), Rsd(10000m), null, null));
    }

    [Fact]
    public void An_exchange_with_both_amounts_said_stores_them_as_said()
    {
        Settle(new(Eur(100m), CurrencyCode.Rsd, 11700m, null, null))
            .Should().Be(new SettledTransfer(Eur(100m), Rsd(11700m), null, null));
    }

    [Fact]
    public void A_rate_quoted_per_unit_of_the_source_currency_multiplies()
    {
        Settle(new(Eur(100m), CurrencyCode.Rsd, null, EuroInDinars, null))
            .Should().Be(new SettledTransfer(Eur(100m), Rsd(11700m), null, null));
    }

    [Fact]
    public void A_rate_quoted_per_unit_of_the_destination_currency_divides()
    {
        Settle(new(Rsd(11700m), CurrencyCode.Eur, null, EuroInDinars, null))
            .Should().Be(new SettledTransfer(Rsd(11700m), Eur(100m), null, null));
    }

    [Fact]
    public void A_converted_amount_is_rounded_to_two_decimals()
    {
        // 33.33 × 117.3456 = 3911.128848
        Settle(new(Eur(33.33m), CurrencyCode.Rsd, null, new ExchangeRate(CurrencyCode.Eur, 117.3456m, CurrencyCode.Rsd), null))
            .To.Should().Be(Rsd(3911.13m));
    }

    [Fact]
    public void The_received_amount_said_wins_over_a_rate()
    {
        Settle(new(Eur(100m), CurrencyCode.Rsd, 11650m, EuroInDinars, null))
            .To.Should().Be(Rsd(11650m));
    }

    [Fact]
    public void A_rate_on_a_same_currency_transfer_is_ignored_even_one_that_could_never_convert()
    {
        Settle(new(Rsd(5000m), CurrencyCode.Rsd, null, EuroInDinars, null)).To.Should().Be(Rsd(5000m));
        Settle(new(Rsd(5000m), CurrencyCode.Rsd, null, new ExchangeRate(CurrencyCode.Rsd, 2m, CurrencyCode.Rsd), null))
            .To.Should().Be(Rsd(5000m));
    }

    [Fact]
    public void An_exchange_with_neither_a_received_amount_nor_a_rate_is_missing_the_received_amount()
    {
        FailureOf(new(Eur(100m), CurrencyCode.Rsd, null, null, null)).Should().Be(RecordFailureReason.MissingReceivedAmount);
    }

    [Theory]
    [InlineData("EUR", "117", "EUR")]
    [InlineData("RSD", "117", "RSD")]
    [InlineData("USD", "117", "RSD")]
    [InlineData("EUR", "117", "USD")]
    [InlineData("EUR", "0", "RSD")]
    [InlineData("EUR", "-117", "RSD")]
    public void A_rate_that_is_not_the_two_legs_currencies_or_not_positive_is_InvalidRate(string baseCode, string quote, string quoteCode)
    {
        var rate = new ExchangeRate(new CurrencyCode(baseCode), decimal.Parse(quote, CultureInfo.InvariantCulture), new CurrencyCode(quoteCode));

        FailureOf(new(Eur(100m), CurrencyCode.Rsd, null, rate, null)).Should().Be(RecordFailureReason.InvalidRate);
    }

    [Fact]
    public void A_fee_on_the_source_not_included_is_added_to_what_left()
    {
        // "снял 10000 с райфа, комиссия 150"
        Settle(new(Rsd(10000m), CurrencyCode.Rsd, null, null, new TransferFeeRequest(Rsd(150m), TransferLeg.From, false)))
            .Should().Be(new SettledTransfer(Rsd(10150m), Rsd(10000m), Rsd(150m), TransferLeg.From));
    }

    [Fact]
    public void A_fee_on_the_source_included_is_inside_what_left()
    {
        // "списали 10150 включая комиссию 150" - the same figures as the line above (acceptance 2)
        Settle(new(Rsd(10150m), CurrencyCode.Rsd, null, null, new TransferFeeRequest(Rsd(150m), TransferLeg.From, true)))
            .Should().Be(new SettledTransfer(Rsd(10150m), Rsd(10000m), Rsd(150m), TransferLeg.From));
    }

    [Fact]
    public void A_fee_on_the_destination_included_is_already_out_of_what_arrived()
    {
        Settle(new(Eur(100m), CurrencyCode.Rsd, 11550m, null, new TransferFeeRequest(Rsd(150m), TransferLeg.To, true)))
            .Should().Be(new SettledTransfer(Eur(100m), Rsd(11550m), Rsd(150m), TransferLeg.To));
    }

    [Fact]
    public void A_fee_on_the_destination_not_included_is_taken_out_of_what_arrived()
    {
        Settle(new(Eur(100m), CurrencyCode.Rsd, 11700m, null, new TransferFeeRequest(Rsd(150m), TransferLeg.To, false)))
            .Should().Be(new SettledTransfer(Eur(100m), Rsd(11550m), Rsd(150m), TransferLeg.To));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_fee_on_the_destination_of_a_converted_amount_comes_out_of_it_whatever_included_says(bool included)
    {
        Settle(new(Eur(100m), CurrencyCode.Rsd, null, EuroInDinars, new TransferFeeRequest(Rsd(150m), TransferLeg.To, included)))
            .Should().Be(new SettledTransfer(Eur(100m), Rsd(11550m), Rsd(150m), TransferLeg.To));
    }

    [Fact]
    public void A_fee_included_in_the_source_is_converted_out_of_the_principal()
    {
        // 98 × 117 = 11466
        Settle(new(Eur(100m), CurrencyCode.Rsd, null, EuroInDinars, new TransferFeeRequest(Eur(2m), TransferLeg.From, true)))
            .Should().Be(new SettledTransfer(Eur(100m), Rsd(11466m), Eur(2m), TransferLeg.From));
    }

    [Fact]
    public void A_fee_not_included_in_the_source_leaves_the_whole_amount_converted()
    {
        Settle(new(Eur(100m), CurrencyCode.Rsd, null, EuroInDinars, new TransferFeeRequest(Eur(2m), TransferLeg.From, false)))
            .Should().Be(new SettledTransfer(Eur(102m), Rsd(11700m), Eur(2m), TransferLeg.From));
    }

    [Fact]
    public void A_fee_of_zero_is_no_fee()
    {
        Settle(new(Rsd(10000m), CurrencyCode.Rsd, null, null, new TransferFeeRequest(Rsd(0m), TransferLeg.From, false)))
            .Should().Be(new SettledTransfer(Rsd(10000m), Rsd(10000m), null, null));
    }

    [Fact]
    public void A_fee_in_another_currency_than_its_leg_is_InvalidFee()
    {
        FailureOf(new(Eur(100m), CurrencyCode.Rsd, 11700m, null, new TransferFeeRequest(Eur(2m), TransferLeg.To, false)))
            .Should().Be(RecordFailureReason.InvalidFee);
        FailureOf(new(Eur(100m), CurrencyCode.Rsd, 11700m, null, new TransferFeeRequest(Rsd(150m), TransferLeg.From, false)))
            .Should().Be(RecordFailureReason.InvalidFee);
    }

    [Fact]
    public void A_negative_fee_is_InvalidFee()
    {
        FailureOf(new(Rsd(10000m), CurrencyCode.Rsd, null, null, new TransferFeeRequest(Rsd(-150m), TransferLeg.From, false)))
            .Should().Be(RecordFailureReason.InvalidFee);
    }

    [Fact]
    public void A_fee_that_eats_the_whole_source_is_InvalidFee()
    {
        FailureOf(new(Rsd(150m), CurrencyCode.Rsd, null, null, new TransferFeeRequest(Rsd(150m), TransferLeg.From, true)))
            .Should().Be(RecordFailureReason.InvalidFee);
    }

    [Fact]
    public void A_fee_larger_than_what_arrived_is_InvalidFee()
    {
        FailureOf(new(Eur(1m), CurrencyCode.Rsd, 100m, null, new TransferFeeRequest(Rsd(150m), TransferLeg.To, false)))
            .Should().Be(RecordFailureReason.InvalidFee);
    }

    [Theory]
    [InlineData("0", null)]
    [InlineData("-100", null)]
    [InlineData("100", "0")]
    [InlineData("100", "-11700")]
    public void An_amount_said_that_is_not_positive_is_InvalidAmount(string from, string? to)
    {
        var request = new TransferRequest(
            Eur(decimal.Parse(from, CultureInfo.InvariantCulture)), CurrencyCode.Rsd,
            to is null ? null : decimal.Parse(to, CultureInfo.InvariantCulture), EuroInDinars, null);

        FailureOf(request).Should().Be(RecordFailureReason.InvalidAmount);
    }

    [Fact]
    public void A_converted_amount_that_rounds_to_nothing_is_InvalidAmount()
    {
        // 0.004 / 117 = 0.0000341… → 0.00
        FailureOf(new(Rsd(0.004m), CurrencyCode.Eur, null, EuroInDinars, null)).Should().Be(RecordFailureReason.InvalidAmount);
    }

    [Fact]
    public void A_rate_converts_only_between_two_different_leg_currencies_in_either_direction()
    {
        new TransferRequest(Eur(100m), CurrencyCode.Rsd, null, EuroInDinars, null).ConvertingRate.Should().Be(EuroInDinars);
        new TransferRequest(Rsd(11700m), CurrencyCode.Eur, 100m, EuroInDinars, null).ConvertingRate.Should().Be(EuroInDinars);

        // HasValue rather than BeNull: a failing BeNull would print the rate, and a rate quoted at zero throws from ToString.
        new TransferRequest(Rsd(5000m), CurrencyCode.Rsd, null, EuroInDinars, null).ConvertingRate.HasValue
            .Should().BeFalse("the legs share one currency");
        new TransferRequest(Rsd(5000m), CurrencyCode.Rsd, null, new ExchangeRate(CurrencyCode.Rsd, 2m, CurrencyCode.Rsd), null)
            .ConvertingRate.HasValue.Should().BeFalse("a rate between the one currency the legs share converts nothing");
        new TransferRequest(Eur(100m), CurrencyCode.Rsd, null, new ExchangeRate(CurrencyCode.Usd, 117m, CurrencyCode.Rsd), null)
            .ConvertingRate.HasValue.Should().BeFalse("the rate is not between the legs' currencies");
        new TransferRequest(Eur(100m), CurrencyCode.Rsd, null, new ExchangeRate(CurrencyCode.Eur, 0m, CurrencyCode.Rsd), null)
            .ConvertingRate.HasValue.Should().BeFalse("a rate quoted at zero converts nothing");
        new TransferRequest(Eur(100m), CurrencyCode.Rsd, null, new ExchangeRate(CurrencyCode.Eur, -117m, CurrencyCode.Rsd), null)
            .ConvertingRate.HasValue.Should().BeFalse("a negative rate converts nothing");
        new TransferRequest(Eur(100m), CurrencyCode.Rsd, 11700m, null, null).ConvertingRate.HasValue
            .Should().BeFalse("no rate was said");
    }
}
