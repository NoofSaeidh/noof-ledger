using AwesomeAssertions;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Fx;
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

    [Fact]
    public void The_synthetic_rates_hold_every_currency_but_EUR_for_every_day_from_four_months_back_through_the_given_day()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var firstDay = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-4);

        var snapshots = MockData.FxRates(today);

        snapshots.Select(snapshot => snapshot.AsOfDate).Should().Equal(
            Enumerable.Range(0, today.DayNumber - firstDay.DayNumber + 1).Select(firstDay.AddDays));
        snapshots.Should().AllSatisfy(snapshot =>
        {
            snapshot.Source.Should().Be(FxSources.OpenErApi);
            snapshot.UnitsPerEur.Keys.Should().BeEquivalentTo(CurrencyCode.Supported.Where(currency => currency != CurrencyCode.Eur));
            snapshot.UnitsPerEur.Values.Should().AllSatisfy(rate => rate.Should().BePositive());
        });
    }

    [Fact]
    public void The_rates_move_in_the_history_months_and_hold_still_from_the_mock_month_on_so_the_net_worth_picture_never_drifts()
    {
        var monthStart = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
        var snapshots = MockData.FxRates(monthStart.AddDays(40));

        var history = snapshots.Where(snapshot => snapshot.AsOfDate < monthStart).Select(snapshot => snapshot.UnitsPerEur[CurrencyCode.Rsd]);
        var current = snapshots.Where(snapshot => snapshot.AsOfDate >= monthStart).ToList();

        history.Distinct().Should().HaveCountGreaterThan(1, "the history months show rates moving day to day");
        current.Should().HaveCount(41);
        current.Should().AllSatisfy(snapshot => snapshot.UnitsPerEur.Should().BeEquivalentTo(new Dictionary<CurrencyCode, decimal>
        {
            [CurrencyCode.Rsd] = 117.1500m,
            [CurrencyCode.Usd] = 1.0850m,
            [CurrencyCode.Rub] = 98.4000m,
            [CurrencyCode.Kzt] = 565.2000m,
        }));
    }
}
