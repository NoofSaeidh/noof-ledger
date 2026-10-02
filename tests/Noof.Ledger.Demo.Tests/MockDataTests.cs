using AwesomeAssertions;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Demo.Tests;

public sealed class MockDataTests
{
    [Fact]
    public void The_logs_window_closes_by_the_first_of_the_month_so_the_hosts_own_rows_never_land_in_the_logs_picture()
    {
        MockData.LogWindowEnd.Should().BeOnOrBefore(new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1));
    }

    [Fact]
    public void Every_mock_log_row_falls_inside_the_logs_window()
    {
        MockData.LogRows.Should().AllSatisfy(row =>
            DateOnly.FromDateTime(row.At.UtcDateTime).Should().BeOnOrAfter(MockData.LogWindowStart).And.BeBefore(MockData.LogWindowEnd));
    }

    [Fact]
    public void Every_log_level_the_logs_page_colours_appears_in_the_window()
    {
        MockData.LogRows.Select(row => row.Level).Distinct().Should().Contain(
            [LogSeverity.Debug, LogSeverity.Information, LogSeverity.Warning, LogSeverity.Error]);
    }

    [Fact]
    public void A_recorded_receipts_lines_add_up_to_its_total_and_the_one_awaiting_confirmation_does_not()
    {
        var receipts = MockData.Records.Where(record => record.Receipt is { Kind: ReceiptKind.Sale }).ToList();

        receipts.Should().HaveCount(3);
        foreach (var record in receipts)
        {
            var receipt = record.Receipt!;
            var addsUp = receipt.Lines.Sum(line => line.Total) == receipt.Total;
            addsUp.Should().Be(record.Status == TransactionStatus.Completed, record.RawText ?? receipt.SellerName);
        }
    }

    [Fact]
    public void The_held_slip_waits_for_record_anyway_only_because_its_amounts_disagree_with_its_printed_rate()
    {
        var record = MockData.Records.Single(candidate => candidate.Id == MockData.HeldSlipTransactionId);
        var slip = record.Receipt!;

        record.Status.Should().Be(TransactionStatus.Captured);
        slip.Kind.Should().Be(ReceiptKind.Exchange);
        slip.Lines.Should().BeEmpty();
        var assessment = slip.Exchange!.Assess(slip.SellerTaxId, taxIdMalformed: false);
        assessment.Disposition.Should().Be(SlipDisposition.Hold);
        assessment.Problems.Should().Equal(SlipProblem.AmountsDisagree);
    }

    [Fact]
    public void A_dinar_receipt_lands_in_the_rsd_wallet_holding_the_default_for_how_it_was_paid()
    {
        MockData.DinarWalletFor(PaymentMethod.Card).Should().Be("Raiffeisen");
        MockData.DinarWalletFor(PaymentMethod.Cash).Should().Be(
            "Cash RSD", "each currency has its own cash default (T-13), and a fiscal receipt is always in dinars");
    }
}
