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
        AppReceipts.ReceiptSource source = AppReceipts.ReceiptSource.FiscalQr, decimal? qrTotal = 373.4567m, decimal total = 373.4567m,
        string? sellerName = "Test Market", string? locationName = "Test Market - Centre",
        IReadOnlyList<AppReceipts.ReceiptLineView>? lines = null) =>
        new(Guid.NewGuid(), source, "SYN-100000001", sellerName, "1 Test Street", locationName, "SYN-1",
            new DateTimeOffset(2026, 9, 25, 9, 30, 0, TimeSpan.Zero), total, CurrencyCode.Rsd, AppReceipts.ReceiptKind.Sale,
            AppReceipts.PaymentMethod.Card, qrTotal, "https://suf.purs.gov.rs/v/?vl=synthetic",
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
        var echo = Echo.ComposeReceipt(Record(), Receipt(source: AppReceipts.ReceiptSource.Vision, qrTotal: 373.4567m));

        echo.Text.Should().Contain("⚠️ Tax Administration unavailable — lines read from the photo");
    }

    [Fact]
    public void Vision_with_no_qr_total_warns_it_was_read_from_the_photo_with_no_fiscal_qr()
    {
        var echo = Echo.ComposeReceipt(Record(), Receipt(source: AppReceipts.ReceiptSource.Vision, qrTotal: null));

        echo.Text.Should().Contain("⚠️ Read from the photo (no fiscal QR)");
        echo.Text.Should().NotContain("Tax Administration unavailable");
    }

    [Fact]
    public void A_fiscal_qr_receipt_with_a_matching_qr_total_carries_no_photo_warning()
    {
        var echo = Echo.ComposeReceipt(Record(), Receipt(source: AppReceipts.ReceiptSource.FiscalQr, qrTotal: 373.4567m));

        echo.Text.Should().NotContain("⚠️");
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
        var echo = Echo.ComposeReceipt(Record(), Receipt(), amountChangeDeclined: true);

        echo.Text.Should().StartWith("Amounts come from the receipt and cannot be changed here — press Cancel if this record is wrong.\n\n");
        echo.Text.Should().Contain("Recorded — Test Market — Test Market - Centre · Cash · balance 0.00 RSD");
    }

    [Fact]
    public void No_declined_amount_change_carries_no_note()
    {
        Echo.ComposeReceipt(Record(), Receipt(), amountChangeDeclined: false).Text.Should().NotContain("cannot be changed here");
    }

    [Theory]
    [InlineData(AppReceipts.ReceiptKind.Copy, "copy")]
    [InlineData(AppReceipts.ReceiptKind.Training, "training")]
    [InlineData(AppReceipts.ReceiptKind.Proforma, "proforma")]
    [InlineData(AppReceipts.ReceiptKind.Advance, "advance")]
    public void A_non_money_receipt_kind_is_reported_as_not_recorded_with_an_edit_action(AppReceipts.ReceiptKind kind, string word)
    {
        var echo = Echo.ComposeReceiptNotRecorded(kind);

        echo.Text.Should().Be($"This receipt is a {word} — not recorded");
        echo.Actions.Should().Equal(RecordAction.Edit);
    }
}
