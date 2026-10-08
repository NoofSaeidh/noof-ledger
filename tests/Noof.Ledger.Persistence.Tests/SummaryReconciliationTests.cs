using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Fx;
using Noof.Ledger.Application.Reporting.Summary;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Reporting;

namespace Noof.Ledger.Persistence.Tests;

// The reader's rows run through the calculator and are held against what the write path posted: per wallet, in its
// own currency, net - moved out + moved in is exactly the sum of its entries for the month.
[Collection("postgres")]
public class SummaryReconciliationTests(PostgresFixture fixture)
{
    static readonly SummaryPeriod September = new(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), Finished: true);

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static Task<SummaryRows> ReadAsync(LedgerDbContext db) =>
        new EfSummaryRowsReader(db).ReadAsync(new DateOnly(2026, 6, 1), new DateOnly(2026, 10, 1), Ct);

    static MonthlySummary WalletMonth(SummaryRows rows, Guid walletId) =>
        MonthlySummaryCalculator.Calculate(rows, new RateTable([]), September, new SummaryScope(walletId), CurrencyCode.Eur);

    static Task<decimal> PostedAsync(LedgerDbContext db, Guid walletId) =>
        db.Database.SqlQuery<decimal>(
            $"""
            SELECT COALESCE(SUM(e.amount), 0) AS "Value"
            FROM entries e
            JOIN transactions t ON t.id = e.transaction_id
            JOIN wallets w ON w.id = e.wallet_id
            WHERE e.wallet_id = {walletId}
              AND e.currency = w.currency
              AND t.status <> {(int)TransactionStatus.Cancelled}
              AND t.occurred_on >= {September.FirstDay}
              AND t.occurred_on <= {September.LastDay}
            """).SingleAsync(Ct);

    [Fact]
    public async Task Each_wallets_spent_received_and_moved_reconcile_to_the_cent_with_what_it_posted()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var m = await SummarySeed.SeedSeptemberAsync(db);
        var rows = await ReadAsync(db);

        (string Name, Guid Id)[] wallets = [("Raiffeisen", m.Raiffeisen), ("Cash RSD", m.CashRsd), ("Wise", m.Wise), ("Cash EUR", m.CashEur)];
        var figures = new List<(string, decimal, decimal, decimal, decimal, decimal)>();
        foreach (var (name, id) in wallets)
        {
            var month = WalletMonth(rows, id);
            var movedOut = month.MovedOut ?? 0m;
            var movedIn = month.MovedIn ?? 0m;

            (month.Net.Amount - movedOut + movedIn).Should().Be(await PostedAsync(db, id), name);
            figures.Add((name, month.Spent.Amount, month.Received.Amount, month.Net.Amount, movedOut, movedIn));
        }

        figures.Should().Equal(
            ("Raiffeisen", 3705.20m, 0m, -3705.20m, 10000.00m, 11700.00m),
            ("Cash RSD", -400.00m, 0m, 400.00m, 0m, 10000.00m),
            ("Wise", 1.00m, 2800.00m, 2799.00m, 299.00m, 0m),
            ("Cash EUR", 2.00m, 0m, -2.00m, 0m, 200.00m));
    }

    [Fact]
    public async Task A_charge_splits_across_its_lines_to_the_cent_and_a_refund_above_its_purchases_leaves_its_category_negative()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var m = await SummarySeed.SeedSeptemberAsync(db);
        var rows = await ReadAsync(db);

        WalletMonth(rows, m.Raiffeisen).Categories.Select(category => (category.CategoryName, category.Amount.Amount))
            .Should().BeEquivalentTo([("Groceries", 2933.33m), ("Coffee", 586.67m), ("Fees & Charges", 185.20m)]);
        var cash = WalletMonth(rows, m.CashRsd);
        cash.Categories.Select(category => (category.CategoryName, category.Amount.Amount))
            .Should().BeEquivalentTo([("Groceries", -300.00m), ("Coffee", -100.00m)]);
        cash.Received.Amount.Should().Be(0m, "a fiscal refund is less spending, never income (SS-19)");
    }

    [Fact]
    public async Task A_refund_moves_from_received_to_spent_and_leaves_net_unchanged()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var m = await SummarySeed.SeedSeptemberAsync(db);
        var asRefund = WalletMonth(await ReadAsync(db), m.CashRsd);

        await db.Database.ExecuteSqlAsync($"DELETE FROM receipts WHERE transaction_id = {m.Refund}", Ct);
        var asIncome = WalletMonth(await ReadAsync(db), m.CashRsd);

        (asRefund.Spent.Amount, asRefund.Received.Amount, asRefund.Net.Amount).Should().Be((-400.00m, 0m, 400.00m));
        (asIncome.Spent.Amount, asIncome.Received.Amount, asIncome.Net.Amount).Should().Be((500.00m, 900.00m, 400.00m));
    }
}
