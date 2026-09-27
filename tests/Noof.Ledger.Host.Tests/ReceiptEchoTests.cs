using AwesomeAssertions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Domain;
using AppReceipts = Noof.Ledger.Application.Receipts;

namespace Noof.Ledger.Host.Tests;

public class ReceiptEchoTests
{
    static readonly IRecordEcho Echo = new RecordEcho();
    static readonly DateOnly Sent = new(2026, 9, 25);

    static RecordedLine Bread => new("Bread", new Money(123.4567m, CurrencyCode.Rsd), "groceries", "Groceries", null);
    static RecordedLine Milk => new("Milk", new Money(250m, CurrencyCode.Rsd), "groceries", "Groceries", null);

    static CategorizationSubject Record(IReadOnlyList<RecordedLine>? lines = null, DateOnly? occurredOn = null) =>
        new(Guid.NewGuid(), string.Empty, 111L, 42, "Cash", TransactionStatus.Completed, Sent, occurredOn ?? Sent,
            lines ?? [Bread, Milk], CaptureKind.Photo, TransactionKind.Expense, CurrencyCode.Rsd, [new Money(0m, CurrencyCode.Rsd)]);

    static AppReceipts.ReceiptView Receipt(
        ReceiptSource source = ReceiptSource.FiscalQr, decimal? qrTotal = 373.4567m, decimal total = 373.4567m,
        string? sellerName = "Test Market", string? locationName = "Test Market - Centre",
        IReadOnlyList<AppReceipts.ReceiptLineView>? lines = null) =>
        new(Guid.NewGuid(), source, "SYN-100000001", sellerName, "1 Test Street", locationName, "SYN-1",
            new DateTimeOffset(2026, 9, 25, 9, 30, 0, TimeSpan.Zero), total, CurrencyCode.Rsd, ReceiptKind.Sale,
            PaymentMethod.Card, qrTotal, "https://suf.purs.gov.rs/v/?vl=synthetic",
            lines ?? [
                new AppReceipts.ReceiptLineView(Guid.NewGuid(), 1, "Bread", 1m, "kom", 123.4567m, 123.4567m, null),
                new AppReceipts.ReceiptLineView(Guid.NewGuid(), 2, "Milk", 2m, "kom", 125m, 250m, null),
            ]);

    [Fact]
    public void A_duplicate_of_a_live_receipt_names_the_original_but_says_nothing_about_restoring_it()
    {
        var echo = Echo.ComposeReceiptDuplicate(new DateOnly(2026, 9, 20), 500m, CurrencyCode.Rsd, originalCancelled: false);

        echo.Text.Should().Be("Already recorded — this receipt was sent before (20.09.2026, 500.00 RSD).");
    }

    // M-11 (Phase 6 final review): the duplicate index does not care about status, so without this the
    // operator has no way to know Restore (not resending the photo) is how to bring the receipt back.
    [Fact]
    public void A_duplicate_of_a_cancelled_receipt_says_so_and_points_at_Restore()
    {
        var echo = Echo.ComposeReceiptDuplicate(new DateOnly(2026, 9, 20), 500m, CurrencyCode.Rsd, originalCancelled: true);

        echo.Text.Should().Be(
            "Already recorded — this receipt was sent before (20.09.2026, 500.00 RSD) and cancelled. "
            + "Press Restore on that message to bring it back.");
    }

    [Fact]
    public void The_receipt_echo_names_the_shop_location_date_wallet_lines_and_total()
    {
        var echo = Echo.ComposeReceipt(Record(), Receipt());

        echo.Text.Should().Be(
            "Recorded — Test Market — Test Market - Centre · Cash · balance 0.00 RSD\n" +
            "Date: 25.09.2026\n" +
            "• Bread — 123.46 RSD · Groceries\n" +
            "• Milk — 250.00 RSD · Groceries\n" +
            "\n" +
            "Total: 373.46 RSD");
        echo.Actions.Should().Equal(RecordAction.Cancel, RecordAction.Edit);
    }

    [Fact]
    public void No_location_omits_it_from_the_shop_header()
    {
        Echo.ComposeReceipt(Record(), Receipt(locationName: null)).Text.Should().StartWith("Recorded — Test Market · Cash");
    }

    [Fact]
    public void No_seller_name_falls_back_to_a_generic_header()
    {
        Echo.ComposeReceipt(Record(), Receipt(sellerName: null, locationName: null)).Text.Should().StartWith("Recorded — Receipt · Cash");
    }

    [Fact]
    public void Vision_with_a_qr_total_warns_that_the_tax_administration_was_unavailable()
    {
        var echo = Echo.ComposeReceipt(Record(), Receipt(source: ReceiptSource.Vision, qrTotal: 373.4567m));

        echo.Text.Should().Contain("⚠️ Tax Administration unavailable — lines read from the photo");
    }

    [Fact]
    public void Vision_with_no_qr_total_warns_it_was_read_from_the_photo_with_no_fiscal_qr()
    {
        var echo = Echo.ComposeReceipt(Record(), Receipt(source: ReceiptSource.Vision, qrTotal: null));

        echo.Text.Should().Contain("⚠️ Read from the photo (no fiscal QR)");
        echo.Text.Should().NotContain("Tax Administration unavailable");
    }

    [Fact]
    public void A_fiscal_qr_receipt_with_a_matching_qr_total_carries_no_photo_warning()
    {
        var echo = Echo.ComposeReceipt(Record(), Receipt(source: ReceiptSource.FiscalQr, qrTotal: 373.4567m));

        echo.Text.Should().NotContain("⚠️");
    }

    // 2026-09-27: a vision-read receipt is only ever a fallback for a photo the QR path could not
    // read reliably - one short line pointing at a way to get an exact read next time, whether or not
    // the lines happen to add up this time. Not "send it as a file": the operator's own real receipts
    // (docs/OPEN-QUESTIONS.md, Phase 6 QR entry) showed a photo, even a full-resolution one sent as a
    // file, does not reliably decode the fiscal QR either - only the link, scanned by the phone's own
    // camera, does.
    [Fact]
    public void A_vision_read_receipt_suggests_the_QR_link_for_an_exact_read_next_time()
    {
        var echo = Echo.ComposeReceipt(Record(), Receipt(source: ReceiptSource.Vision, qrTotal: null));

        echo.Text.Should().Contain(
            "⚠️ For an exact read next time, send the link from the receipt's QR code (scan it with your phone camera) instead of a photo.");
    }

    [Fact]
    public void A_fiscal_qr_receipt_never_carries_the_exact_read_hint()
    {
        var echo = Echo.ComposeReceipt(Record(), Receipt(source: ReceiptSource.FiscalQr));

        echo.Text.Should().NotContain("For an exact read next time");
    }

    [Fact]
    public void Lines_that_disagree_with_the_receipt_total_by_more_than_a_cent_are_warned_about()
    {
        var echo = Echo.ComposeReceipt(Record(), Receipt(total: 400.00m));

        echo.Text.Should().Contain("⚠️ Lines add up to 373.46 RSD, the receipt says 400.00 RSD");
    }

    [Fact]
    public void A_one_cent_rounding_difference_is_not_warned_about()
    {
        var echo = Echo.ComposeReceipt(Record(), Receipt(total: 373.4667m));

        echo.Text.Should().NotContain("⚠️ Lines add up to");
    }

    [Fact]
    public void More_than_forty_lines_are_grouped_by_category_after_the_first_forty()
    {
        var lines = Enumerable.Range(1, 60)
            .Select(i => new RecordedLine($"Item {i}", new Money(10m, CurrencyCode.Rsd), "groceries", "Groceries", null))
            .ToList();
        var receiptLines = Enumerable.Range(1, 60)
            .Select(i => new AppReceipts.ReceiptLineView(Guid.NewGuid(), i, $"Item {i}", 1m, null, 10m, 10m, null))
            .ToList();

        var echo = Echo.ComposeReceipt(Record(lines: lines), Receipt(total: 600m, lines: receiptLines));

        echo.Text.Length.Should().BeLessThanOrEqualTo(4096);
        echo.Text.Should().Contain("• Item 1 — 10.00 RSD · Groceries");
        echo.Text.Should().Contain("• Item 40 — 10.00 RSD · Groceries");
        echo.Text.Should().NotContain("Item 41 —");
        echo.Text.Should().Contain("… 20 more lines");
        echo.Text.Should().Contain("Groceries: 200.00 RSD");
    }

    // N-5 (Phase 6 re-review): M-4 and M-11 can both leave RecordActionHandler re-rendering a receipt
    // record that is Captured (a non-money slip restored to before it was Cancelled and Persisted
    // never ran) or Failed (a Failed receipt transaction, restored). Neither is "Recorded … Total:".
    [Fact]
    public void A_captured_receipt_record_renders_the_waiting_text_not_Recorded()
    {
        var record = Record() with { Status = TransactionStatus.Captured };

        var echo = Echo.ComposeReceipt(record, Receipt());

        echo.Text.Should().NotContain("Recorded");
        echo.Text.Should().NotContain("Total:");
        echo.Actions.Should().BeEmpty();
    }

    // R2-4 (Phase 6 second re-review): a Copy/Training/Proforma/Advance slip Captured with no job
    // pending (M-4's Cancel then a Restore) used to fall into Waiting(record) - "Recording…", no
    // actions - a dead end promising work nobody will do, with no [Edit] to record it by hand.
    [Fact]
    public void A_captured_non_money_receipt_renders_the_not_recorded_text_with_an_edit_action()
    {
        var record = Record() with { Status = TransactionStatus.Captured };

        var echo = Echo.ComposeReceipt(record, Receipt() with { Kind = ReceiptKind.Copy });

        echo.Text.Should().Be(Echo.ComposeReceiptNotRecorded(ReceiptKind.Copy).Text);
        echo.Actions.Should().Equal(RecordAction.Edit);
    }

    [Fact]
    public void A_failed_receipt_record_renders_the_ordinary_failure_message()
    {
        var record = Record() with { Status = TransactionStatus.Failed };

        var echo = Echo.ComposeReceipt(record, Receipt());

        echo.Should().Be(Echo.Failure);
    }

    [Fact]
    public void A_cancelled_receipt_transaction_keeps_the_shop_header_and_offers_restore()
    {
        var record = Record() with { Status = TransactionStatus.Cancelled };

        var echo = Echo.ComposeReceipt(record, Receipt());

        echo.Text.Should().StartWith("Cancelled — Test Market — Test Market - Centre · Cash · balance 0.00 RSD");
        echo.Text.Should().Contain("• Bread — 123.46 RSD · Groceries");
        echo.Actions.Should().Equal(RecordAction.Restore);
    }

    [Fact]
    public void A_declined_amount_change_is_noted_ahead_of_the_receipt_body()
    {
        var echo = Echo.ComposeReceipt(Record(), Receipt(), AppReceipts.UnsupportedChangeKind.Amount);

        echo.Text.Should().StartWith("Amounts come from the receipt and cannot be changed here — press Cancel if this record is wrong.\n\n");
        echo.Text.Should().Contain("Recorded — Test Market — Test Market - Centre · Cash · balance 0.00 RSD");
    }

    // N-8 (Phase 6 re-review): a date request earns its own warning, distinct from the amount one.
    [Fact]
    public void A_declined_date_change_is_noted_ahead_of_the_receipt_body()
    {
        var echo = Echo.ComposeReceipt(Record(), Receipt(), AppReceipts.UnsupportedChangeKind.Date);

        echo.Text.Should().StartWith("The date comes from the receipt and cannot be changed here — press Cancel if this record is wrong.\n\n");
        echo.Text.Should().Contain("Recorded — Test Market — Test Market - Centre · Cash · balance 0.00 RSD");
    }

    [Fact]
    public void No_declined_change_carries_no_note()
    {
        Echo.ComposeReceipt(Record(), Receipt(), AppReceipts.UnsupportedChangeKind.None).Text.Should().NotContain("cannot be changed here");
    }

    // 2026-09-27: Cancel on a receipt still awaiting confirmation has no categorised line items -
    // record.Lines is empty, since CategorizeReceipt never ran. This must show the receipt's own
    // stored total, never fall back to ComposeReceipt's "Total: " (with nothing after it) and a false
    // "Lines add up to 0.00" mismatch warning.
    [Fact]
    public void A_cancelled_unconfirmed_receipt_shows_the_receipts_own_total_and_offers_only_restore()
    {
        var record = Record(lines: []) with { Status = TransactionStatus.Cancelled };

        var echo = Echo.ComposeReceiptCancelledUnconfirmed(record, Receipt(total: 500m));

        echo.Text.Should().StartWith("Cancelled — Test Market — Test Market - Centre · Cash · balance 0.00 RSD");
        echo.Text.Should().Contain("500.00 RSD");
        echo.Text.Should().NotContain("Total: \n", "the M-8 empty-total look must never come from this rendering");
        echo.Text.Should().NotContain("Lines add up to");
        echo.Actions.Should().Equal(RecordAction.Restore);
    }

    static AppReceipts.ExtractedReceipt NeedsConfirmationReceipt(
        DateTimeOffset? issuedAt = null, string? sellerTaxId = null, string? fiscalNumber = null) => new(
        ReceiptSource.Vision, VerificationUrl: null, sellerTaxId, "Test Market", SellerAddress: null,
        LocationName: null, fiscalNumber, issuedAt, 500m, CurrencyCode.Rsd, ReceiptKind.Sale, PaymentMethod.Card,
        QrTotal: null,
        [new AppReceipts.ExtractedReceiptLine(1, "Bread", 1m, null, 400m, 400m, null)]);

    // 2026-09-27 finding: every other vision echo carries the decision-5 QR hint and, where read,
    // the date and PIB/fiscal number - the confirmation prompt is a vision echo too and showed neither.
    [Fact]
    public void ComposeReceiptNeedsConfirmation_carries_the_QR_hint()
    {
        var echo = Echo.ComposeReceiptNeedsConfirmation(NeedsConfirmationReceipt());

        echo.Text.Should().Contain(
            "⚠️ For an exact read next time, send the link from the receipt's QR code (scan it with your phone camera) instead of a photo.");
    }

    [Fact]
    public void ComposeReceiptNeedsConfirmation_shows_the_date_PIB_and_fiscal_number_when_they_were_read()
    {
        var receipt = NeedsConfirmationReceipt(
            issuedAt: new DateTimeOffset(2026, 9, 25, 9, 30, 0, TimeSpan.Zero), sellerTaxId: "123456789",
            fiscalNumber: "2WJCQFGP-2WJCQFGP-66360");

        var echo = Echo.ComposeReceiptNeedsConfirmation(receipt);

        echo.Text.Should().Contain("Date: 25.09.2026");
        echo.Text.Should().Contain("PIB: 123456789");
        echo.Text.Should().Contain("Fiscal #: 2WJCQFGP-2WJCQFGP-66360");
    }

    [Fact]
    public void ComposeReceiptNeedsConfirmation_omits_the_date_PIB_and_fiscal_number_when_none_were_read()
    {
        var echo = Echo.ComposeReceiptNeedsConfirmation(NeedsConfirmationReceipt());

        echo.Text.Should().NotContain("Date:");
        echo.Text.Should().NotContain("PIB:");
        echo.Text.Should().NotContain("Fiscal #:");
    }

    [Fact]
    public void ComposeReceiptNeedsConfirmation_names_a_malformed_tax_id()
    {
        var echo = Echo.ComposeReceiptNeedsConfirmation(NeedsConfirmationReceipt(), taxIdMalformed: true);

        echo.Text.Should().Contain("⚠️ The printed tax id does not look like a valid PIB (9 digits)");
    }

    // The ReceiptView overload (RecordActionHandler's Cancel/Restore, ExtractReceiptWorker's own C-1
    // replay) must produce the identical prompt a fresh ExtractedReceipt would have - same arithmetic,
    // same wording, read from what was actually stored instead of a live model call.
    [Fact]
    public void ComposeReceiptNeedsConfirmation_from_a_stored_ReceiptView_matches_the_ExtractedReceipt_wording()
    {
        var view = Receipt(source: ReceiptSource.Vision, qrTotal: null, total: 500m,
            lines: [new AppReceipts.ReceiptLineView(Guid.NewGuid(), 1, "Bread", 1m, "kom", 400m, 400m, null)]);

        var echo = Echo.ComposeReceiptNeedsConfirmation(view);

        echo.Text.Should().Contain("⚠️ Lines add up to 400.00 RSD, the receipt says 500.00 RSD");
        echo.Text.Should().Contain(
            "⚠️ For an exact read next time, send the link from the receipt's QR code (scan it with your phone camera) instead of a photo.");
        echo.Actions.Should().Equal(RecordAction.RecordAnyway, RecordAction.Cancel);
    }

    // Copilot finding on PR #3, RecordEcho.cs:~200: unlike the recorded-echo path (40-line cap above),
    // the confirmation path listed every receipt line - a large unreadable/mismatched vision receipt
    // could exceed Telegram's 4096-character limit and the operator would lose Record anyway/Cancel.
    [Fact]
    public void A_sixty_line_confirmation_echo_caps_detailed_lines_at_forty_and_summarises_the_rest()
    {
        var lines = Enumerable.Range(1, 60)
            .Select(i => new AppReceipts.ExtractedReceiptLine(
                i, $"A rather long line item name number {i}", 1m, null, 10m, 10m, null))
            .ToList();
        var receipt = NeedsConfirmationReceipt() with { Total = 600m, Lines = lines };

        var echo = Echo.ComposeReceiptNeedsConfirmation(receipt);

        echo.Text.Length.Should().BeLessThanOrEqualTo(4096);
        echo.Text.Should().Contain("• A rather long line item name number 1 —");
        echo.Text.Should().Contain("• A rather long line item name number 40 —");
        echo.Text.Should().NotContain("A rather long line item name number 41 —");
        echo.Text.Should().Contain("… 20 more lines");
        echo.Actions.Should().Equal(RecordAction.RecordAnyway, RecordAction.Cancel);
    }

    [Theory]
    [InlineData(ReceiptKind.Copy, "copy")]
    [InlineData(ReceiptKind.Training, "training")]
    [InlineData(ReceiptKind.Proforma, "proforma")]
    [InlineData(ReceiptKind.Advance, "advance")]
    public void A_non_money_receipt_kind_is_reported_as_not_recorded_with_an_edit_action(ReceiptKind kind, string word)
    {
        var echo = Echo.ComposeReceiptNotRecorded(kind);

        echo.Text.Should().Be($"This receipt is a {word} — not recorded");
        echo.Actions.Should().Equal(RecordAction.Edit);
    }
}
