using System.Globalization;
using AwesomeAssertions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Host.Tests;

public class SlipEchoTests
{
    static readonly IRecordEcho Echo = new RecordEcho();
    static readonly DateOnly Sent = new(2026, 9, 27);
    static readonly Guid CashEurId = Guid.Parse("00000000-0000-0000-0007-0000000000e1");
    static readonly Guid CashRsdId = Guid.Parse("00000000-0000-0000-0007-0000000000d1");
    static readonly ExtractedExchange CleanEvidence = new(100.00m, "EUR", 11700.00m, "RSD", 117.0000m, null, null, "PZ-2026-0917");
    const string SlipRows = "\nMenjačnica Zlatnik · from a slip photo\n⚠️ Read from the slip photo — check the figures.";

    static CategorizationSubject SlipRecord(
        TransactionStatus status = TransactionStatus.Completed, string? venueName = "Menjačnica Zlatnik", bool withSlip = true) =>
        new(Guid.NewGuid(), string.Empty, 111L, 42, "Cash EUR", status, Sent, Sent, [], CaptureKind.Photo, TransactionKind.Transfer,
            CurrencyCode.Eur, [new Money(150.00m, CurrencyCode.Eur)], WalletId: CashEurId,
            Transfer: new TransferView(
                CashEurId, "Cash EUR", new Money(100.00m, CurrencyCode.Eur), CashRsdId, "Cash RSD", new Money(11700.00m, CurrencyCode.Rsd),
                Fee: null, FeeLeg: null, StatedRate: new ExchangeRate(CurrencyCode.Eur, 117.0000m, CurrencyCode.Rsd), VenueName: venueName,
                FromBalances: [new Money(150.00m, CurrencyCode.Eur)], ToBalances: [new Money(23700.00m, CurrencyCode.Rsd)]),
            Slip: withSlip ? new SlipFacts(venueName, "PZ-2026-0917", CleanEvidence) : null);

    static CategorizationSubject IncompleteSlip(ExtractedExchange evidence) =>
        new(Guid.NewGuid(), string.Empty, 111L, 42, string.Empty, TransactionStatus.Failed, Sent, Sent, [], CaptureKind.Photo,
            FailureReason: RecordFailureReason.SlipIncomplete, Slip: new SlipFacts("Menjačnica Zlatnik", "PZ-2026-0917", evidence));

    static ExchangeSlipView HeldSlip(
        string? sellerTaxId = null, string? slipNumber = "PZ-2026-0917", ExtractedExchange? evidence = null) => new(
        Guid.Parse("00000000-0000-0000-0007-000000000030"), sellerTaxId, "Menjačnica Zlatnik",
        new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero), slipNumber,
        evidence ?? new ExtractedExchange(100.00m, "EUR", 11650.00m, "RSD", 117.0000m, null, null, slipNumber));

    [Fact]
    public void A_recorded_slip_echoes_the_exchange_then_its_office_and_the_slip_warning()
    {
        var echo = Echo.Compose(SlipRecord());

        echo.Text.Should().Contain("100.00 EUR (Cash EUR) → 11700.00 RSD (Cash RSD)");
        echo.Text.Should().EndWith(SlipRows);
        echo.Text.Should().NotContain("QR", "the fiscal QR advice means nothing for a slip");
        echo.Actions.Should().Equal(RecordAction.Cancel, RecordAction.Edit);
    }

    [Fact]
    public void A_cancelled_slip_keeps_its_office_and_the_slip_warning()
    {
        var echo = Echo.Compose(SlipRecord(TransactionStatus.Cancelled));

        echo.Text.Should().EndWith(SlipRows);
        echo.Actions.Should().Equal(RecordAction.Restore);
    }

    [Theory]
    [InlineData("Zlatnik", "Menjačnica Zlatnik · from a slip photo")]
    [InlineData("MENJAČNICA ZLATNIK", "MENJAČNICA ZLATNIK · from a slip photo")]
    [InlineData("Menjacnica Zlatnik", "Menjacnica Zlatnik · from a slip photo")]
    [InlineData(null, "Menjačnica · from a slip photo")]
    public void The_office_is_called_a_menjacnica_once(string? venueName, string venueLine)
    {
        Echo.Compose(SlipRecord(venueName: venueName)).Text
            .Should().EndWith($"\n{venueLine}\n⚠️ Read from the slip photo — check the figures.");
    }

    [Fact]
    public void A_transfer_with_no_slip_has_no_slip_rows()
    {
        Echo.Compose(SlipRecord(withSlip: false)).Text.Should().NotContain("from a slip photo");
    }

    [Fact]
    public void An_incomplete_slip_asks_for_the_figure_assess_found_missing()
    {
        var echo = Echo.Compose(IncompleteSlip(new ExtractedExchange(100.00m, "EUR", null, "RSD", null, null, null, "PZ-2026-0917")));

        echo.Text.Should().Be("Slip read, but the amount received is unreadable — reply with it.");
    }

    [Fact]
    public void Every_missing_figure_is_asked_for_in_one_sentence()
    {
        var echo = Echo.Compose(IncompleteSlip(new ExtractedExchange(null, "EUR", null, null, 117.0000m, null, null, "PZ-2026-0917")));

        echo.Text.Should().Be("Slip read, but the amount given, the amount received, the currency received is unreadable — reply with it.");
    }

    [Fact]
    public void A_currency_the_ledger_does_not_hold_is_asked_for_as_unreadable()
    {
        var echo = Echo.Compose(IncompleteSlip(new ExtractedExchange(100.00m, "CHF", null, "RSD", 117.0000m, null, null, "PZ-2026-0917")));

        echo.Text.Should().Be("Slip read, but the currency given, the amount received is unreadable — reply with it.");
    }

    [Fact]
    public void The_slip_confirmation_prompt_shows_what_was_read_and_why_it_is_held()
    {
        var echo = Echo.ComposeSlipNeedsConfirmation(HeldSlip());

        echo.Text.Should().Be(
            "This exchange slip doesn't look right — Menjačnica Zlatnik\n" +
            "Slip #: PZ-2026-0917\n" +
            "Given: 100.00 EUR\n" +
            "Received: 11650.00 RSD\n" +
            "Rate: 1 EUR = 117.0000 RSD\n" +
            "\n" +
            "⚠️ The given and received amounts don't match the printed rate\n" +
            "⚠️ The office's PIB is unreadable or not 9 digits\n" +
            "⚠️ Read from the slip photo — check the figures.\n" +
            "\n" +
            "Record it anyway, or cancel?");
        echo.Actions.Should().Equal(RecordAction.RecordAnyway, RecordAction.Cancel);
    }

    [Fact]
    public void The_prompt_names_a_read_PIB_and_a_commission_printed_with_no_currency_in_dinars()
    {
        var echo = Echo.ComposeSlipNeedsConfirmation(HeldSlip(
            sellerTaxId: "123456789", slipNumber: null,
            evidence: new ExtractedExchange(11850.00m, "RSD", 100.00m, "EUR", 117.0000m, 150.00m, null, null)));

        echo.Text.Should().Be(
            "This exchange slip doesn't look right — Menjačnica Zlatnik\n" +
            "PIB: 123456789\n" +
            "Given: 11850.00 RSD\n" +
            "Received: 100.00 EUR\n" +
            "Rate: 1 EUR = 117.0000 RSD\n" +
            "Commission: 150.00 RSD\n" +
            "\n" +
            "⚠️ The slip number is unreadable, so a repeat of this slip can't be caught\n" +
            "⚠️ Read from the slip photo — check the figures.\n" +
            "\n" +
            "Record it anyway, or cancel?");
    }

    [Fact]
    public void Cancel_before_confirming_shows_the_slip_was_never_recorded_and_offers_restore()
    {
        var echo = Echo.ComposeSlipCancelledUnconfirmed(HeldSlip());

        echo.Text.Should().Be(
            "Cancelled — Menjačnica Zlatnik\n" +
            "Given: 100.00 EUR\n" +
            "Received: 11650.00 RSD\n" +
            "Rate: 1 EUR = 117.0000 RSD\n" +
            "\n" +
            "This slip was never recorded — press Restore to bring it back for confirmation.");
        echo.Actions.Should().Equal(RecordAction.Restore);
    }

    [Fact]
    public void The_duplicate_echo_names_the_earlier_slips_day_and_figures()
    {
        Echo.ComposeSlipDuplicate(new DateOnly(2026, 9, 25), CleanEvidence).Should().BeEquivalentTo(new EchoMessage(
            "Already recorded — this exchange slip was sent before (25.09.2026, 100.00 EUR → 11700.00 RSD).", []));
        Echo.ComposeSlipDuplicate(new DateOnly(2026, 9, 25), CleanEvidence, originalCancelled: true).Text.Should().Be(
            "Already recorded — this exchange slip was sent before (25.09.2026, 100.00 EUR → 11700.00 RSD) and cancelled. "
            + "Press Restore on that message to bring it back.");
        Echo.ComposeSlipDuplicate(null, new ExtractedExchange(null, null, null, null, null, null, null, "PZ-1")).Text
            .Should().Be("Already recorded — this exchange slip was sent before.");
    }

    [Fact]
    public void A_clean_slip_is_acknowledged_as_being_recorded()
    {
        Echo.RecordingExchange.Should().Be("Recording the exchange…");
    }

    [Fact]
    public void A_slip_handed_to_the_receipt_echo_is_echoed_as_the_slip_never_a_shop_receipt()
    {
        var record = SlipRecord();
        var receipt = new ReceiptView(
            Guid.NewGuid(), ReceiptSource.Vision, "123456789", "Menjačnica Zlatnik", null, null, null,
            new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero), 11700.00m, CurrencyCode.Rsd, ReceiptKind.Exchange,
            null, null, null, []);

        var echo = Echo.ComposeReceipt(record, receipt);

        echo.Text.Should().Be(Echo.Compose(record).Text);
        echo.Text.Should().NotContain("Lines add up to");
    }

    [Fact]
    public void The_prompt_renders_the_same_under_a_Serbian_machine_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sr-Latn-RS");
        try
        {
            Echo.ComposeSlipNeedsConfirmation(HeldSlip()).Text.Should().Contain("Received: 11650.00 RSD\nRate: 1 EUR = 117.0000 RSD");
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }
}
