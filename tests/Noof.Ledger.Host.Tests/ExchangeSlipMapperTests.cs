using AwesomeAssertions;
using NSubstitute;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Domain;
using Noof.Ledger.Host.Workers;

namespace Noof.Ledger.Host.Tests;

public class ExchangeSlipMapperTests
{
    static ExtractedExchange Slip(
        decimal? given = 100.00m, string? givenCurrency = "EUR", decimal? received = 11700.00m, string? receivedCurrency = "RSD",
        decimal? rate = 117.0000m, decimal? commission = null, string? commissionCurrency = null,
        string? slipNumber = "PZ-2026-0917") =>
        new(given, givenCurrency, received, receivedCurrency, rate, commission, commissionCurrency, slipNumber);

    static SettledTransfer Settle(ExtractedExchange slip)
    {
        ExchangeSlipMapper.TryRead(slip, out var request, out var readFailure).Should().BeTrue($"the slip reads ({readFailure})");
        request!.TrySettle(out var settled, out var settleFailure).Should().BeTrue($"the transfer settles ({settleFailure})");
        return settled!;
    }

    [Fact]
    public void A_sale_of_euros_reads_as_a_transfer_from_the_euros_given_to_the_dinars_received()
    {
        ExchangeSlipMapper.TryRead(Slip(), out var request, out _).Should().BeTrue();

        request.Should().Be(new TransferRequest(
            new Money(100.00m, CurrencyCode.Eur), CurrencyCode.Rsd, 11700.00m,
            new ExchangeRate(CurrencyCode.Eur, 117.0000m, CurrencyCode.Rsd), null));
    }

    [Fact]
    public void A_purchase_of_euros_reads_the_printed_rate_with_the_euro_as_its_base()
    {
        ExchangeSlipMapper.TryRead(
                Slip(given: 11750.00m, givenCurrency: "RSD", received: 100.00m, receivedCurrency: "EUR", rate: 117.5000m),
                out var request, out _)
            .Should().BeTrue();

        request!.Rate.Should().Be(new ExchangeRate(CurrencyCode.Eur, 117.5000m, CurrencyCode.Rsd));
    }

    [Fact]
    public void A_missing_received_amount_is_filled_from_the_rate()
    {
        Settle(Slip(received: null)).To.Should().Be(new Money(11700.00m, CurrencyCode.Rsd));
        Settle(Slip(given: 11750.00m, givenCurrency: "RSD", received: null, receivedCurrency: "EUR", rate: 117.5000m))
            .To.Should().Be(new Money(100.00m, CurrencyCode.Eur));
    }

    [Fact]
    public void A_dinar_commission_on_a_purchase_is_the_fee_on_the_from_leg_inside_the_amount_given()
    {
        Settle(Slip(given: 11850.00m, givenCurrency: "RSD", received: 100.00m, receivedCurrency: "EUR", rate: 117.0000m,
                commission: 150.00m, commissionCurrency: "RSD"))
            .Should().Be(new SettledTransfer(
                new Money(11850.00m, CurrencyCode.Rsd), new Money(100.00m, CurrencyCode.Eur),
                new Money(150.00m, CurrencyCode.Rsd), TransferLeg.From));
    }

    [Fact]
    public void A_dinar_commission_on_a_sale_is_the_fee_on_the_to_leg_inside_the_amount_received()
    {
        Settle(Slip(received: 11550.00m, commission: 150.00m, commissionCurrency: "RSD"))
            .Should().Be(new SettledTransfer(
                new Money(100.00m, CurrencyCode.Eur), new Money(11550.00m, CurrencyCode.Rsd),
                new Money(150.00m, CurrencyCode.Rsd), TransferLeg.To));
    }

    [Fact]
    public void With_the_received_amount_unread_a_dinar_commission_comes_off_what_the_rate_gives()
    {
        Settle(Slip(received: null, commission: 150.00m, commissionCurrency: "RSD"))
            .To.Should().Be(new Money(11550.00m, CurrencyCode.Rsd));
    }

    [Fact]
    public void A_commission_printed_with_no_currency_is_read_as_dinars()
    {
        var settled = Settle(Slip(received: 11550.00m, commission: 150.00m, commissionCurrency: null));

        settled.Fee.Should().Be(new Money(150.00m, CurrencyCode.Rsd));
        settled.FeeLeg.Should().Be(TransferLeg.To);
    }

    [Fact]
    public void A_commission_in_a_third_currency_cannot_be_placed()
    {
        ExchangeSlipMapper.TryRead(Slip(commission: 1.30m, commissionCurrency: "USD"), out var request, out var failure)
            .Should().BeFalse();

        request.Should().BeNull();
        failure.Should().Be(RecordFailureReason.InvalidFee);
    }

    public static TheoryData<ExtractedExchange> SlipsMissingWhatTheTransferNeeds => new()
    {
        Slip(given: null),
        Slip(given: 0m),
        Slip(givenCurrency: null),
        Slip(givenCurrency: "CHF"),
        Slip(receivedCurrency: null),
    };

    [Theory]
    [MemberData(nameof(SlipsMissingWhatTheTransferNeeds))]
    public void An_unread_given_amount_or_either_currency_is_SlipIncomplete(ExtractedExchange slip)
    {
        ExchangeSlipMapper.TryRead(slip, out _, out var failure).Should().BeFalse();

        failure.Should().Be(RecordFailureReason.SlipIncomplete);
    }

    [Fact]
    public void A_received_amount_neither_printed_nor_computable_is_MissingReceivedAmount()
    {
        ExchangeSlipMapper.TryRead(Slip(received: null, rate: null), out var request, out _).Should().BeTrue();

        request!.TrySettle(out _, out var failure).Should().BeFalse();
        failure.Should().Be(RecordFailureReason.MissingReceivedAmount);
    }

    [Fact]
    public void A_rate_between_two_foreign_currencies_is_never_used()
    {
        ExchangeSlipMapper.TryRead(Slip(givenCurrency: "EUR", received: 108.00m, receivedCurrency: "USD", rate: 1.0800m), out var request, out _)
            .Should().BeTrue();

        request!.Rate.Should().BeNull();
    }

    public static TheoryData<ExtractedExchange> SlipsOfEveryShape => new()
    {
        Slip(),
        Slip(received: null),
        Slip(received: null, rate: null),
        Slip(received: 0m, rate: null),
        Slip(given: null),
        Slip(given: 0m),
        Slip(givenCurrency: null),
        Slip(givenCurrency: "CHF"),
        Slip(receivedCurrency: null),
        Slip(givenCurrency: "EUR", received: null, receivedCurrency: "USD", rate: 1.0800m),
        Slip(given: 11750.00m, givenCurrency: "RSD", received: null, receivedCurrency: "EUR", rate: 117.5000m),
    };

    // The mapper reads currencies and the rate through ExtractedExchange's own rules, but its amount checks (a given
    // amount must be positive, a received one that is not is left for TrySettle to fill) are its own; this pins
    // them to Assess: a slip is incomplete exactly when the mapping fails for want of a figure.
    [Theory]
    [MemberData(nameof(SlipsOfEveryShape))]
    public void A_slip_is_incomplete_exactly_when_the_mapping_lacks_a_figure(ExtractedExchange slip)
    {
        var incomplete = slip.Assess("123456789", taxIdMalformed: false).Disposition == SlipDisposition.Incomplete;

        var lacksAFigure = !ExchangeSlipMapper.TryRead(slip, out var request, out var failure)
            ? failure == RecordFailureReason.SlipIncomplete
            : !request.TrySettle(out _, out failure) && failure == RecordFailureReason.MissingReceivedAmount;

        lacksAFigure.Should().Be(incomplete);
    }

    static readonly WalletOption CashRsd = new(
        Guid.Parse("00000000-0000-0000-0007-0000000000d1"), "Cash RSD", CurrencyCode.Rsd, [], IsDefaultForCurrency: false,
        DefaultForPayment: WalletPaymentDefault.Cash);
    static readonly WalletOption RaiffeisenRsd = new(
        Guid.Parse("00000000-0000-0000-0007-0000000000d2"), "Raiffeisen RSD", CurrencyCode.Rsd, [], IsDefaultForCurrency: true,
        DefaultForPayment: WalletPaymentDefault.Card);
    static readonly WalletOption WiseEur = new(
        Guid.Parse("00000000-0000-0000-0007-0000000000e2"), "Wise EUR", CurrencyCode.Eur, [], IsDefaultForCurrency: true);
    static readonly WalletOption CashEur = new(
        Guid.Parse("00000000-0000-0000-0007-0000000000e1"), "Cash EUR", CurrencyCode.Eur, [], IsDefaultForCurrency: false,
        DefaultForPayment: WalletPaymentDefault.Cash);

    [Fact]
    public void A_legs_wallet_is_the_cash_default_of_its_currency()
    {
        IReadOnlyList<WalletOption> wallets = [RaiffeisenRsd, CashRsd, WiseEur, CashEur];

        ExchangeSlipMapper.CashWalletOf(wallets, CurrencyCode.Rsd).Should().Be(CashRsd.Id, "the cash default wins over the RSD default (a card wallet)");
        ExchangeSlipMapper.CashWalletOf(wallets, CurrencyCode.Eur).Should().Be(CashEur.Id);
    }

    [Fact]
    public void A_currency_with_no_cash_default_puts_the_leg_on_that_currencys_default()
    {
        IReadOnlyList<WalletOption> wallets = [RaiffeisenRsd, WiseEur];

        ExchangeSlipMapper.CashWalletOf(wallets, CurrencyCode.Rsd).Should().Be(RaiffeisenRsd.Id);
        ExchangeSlipMapper.CashWalletOf(wallets, CurrencyCode.Kzt).Should().BeNull();
    }

    [Fact]
    public async Task The_office_is_the_venue_its_PIB_names()
    {
        var merchants = Substitute.For<IMerchantDirectory>();
        var venueId = Guid.Parse("00000000-0000-0000-0007-000000000003");
        merchants.VenueForTaxIdAsync("123456789", "Menjačnica Zlatnik", Arg.Any<CancellationToken>()).Returns(venueId);
        var slip = new ExchangeSlipView(Guid.NewGuid(), "123456789", "Menjačnica Zlatnik", null, "PZ-2026-0917", Slip());

        (await ExchangeSlipMapper.VenueOfAsync(slip, merchants, TestContext.Current.CancellationToken)).Should().Be(venueId);
    }

    [Fact]
    public async Task An_office_with_no_printed_name_is_named_by_its_PIB_and_one_with_no_PIB_has_no_venue()
    {
        var merchants = Substitute.For<IMerchantDirectory>();
        merchants.VenueForTaxIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());

        await ExchangeSlipMapper.VenueOfAsync(
            new ExchangeSlipView(Guid.NewGuid(), "123456789", null, null, null, Slip()), merchants, TestContext.Current.CancellationToken);
        var none = await ExchangeSlipMapper.VenueOfAsync(
            new ExchangeSlipView(Guid.NewGuid(), null, "Menjačnica Zlatnik", null, null, Slip()), merchants, TestContext.Current.CancellationToken);

        await merchants.Received(1).VenueForTaxIdAsync("123456789", "123456789", Arg.Any<CancellationToken>());
        none.Should().BeNull();
    }
}
