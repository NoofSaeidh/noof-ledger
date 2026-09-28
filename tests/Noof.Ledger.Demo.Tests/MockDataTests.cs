using AwesomeAssertions;
using Noof.Ledger.Application.Diagnostics;
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
        var receipts = MockData.Records.Where(record => record.Receipt is not null).ToList();

        receipts.Should().HaveCount(3);
        foreach (var record in receipts)
        {
            var receipt = record.Receipt!;
            var addsUp = receipt.Lines.Sum(line => line.Total) == receipt.Total;
            addsUp.Should().Be(record.Status == TransactionStatus.Completed, record.RawText ?? receipt.SellerName);
        }
    }
}
