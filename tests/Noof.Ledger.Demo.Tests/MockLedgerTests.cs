using System.Globalization;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Diagnostics.BugReports;
using Noof.Ledger.Application.Diagnostics.Integrity;
using Noof.Ledger.Application.Reporting;
using Noof.Ledger.Application.Wallets;
using Noof.Ledger.Domain;
using Npgsql;

namespace Noof.Ledger.Demo.Tests;

[Trait("Category", "Database")]
public sealed class MockLedgerTests(DemoTestDatabase database) : IClassFixture<DemoTestDatabase>
{
    [Fact]
    public async Task Every_wallet_ends_the_mock_month_on_its_hand_computed_balance()
    {
        if (database.Unavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await Refresh.RunAsync(database.Admin, database.Name, database.Paths, TestContext.Current.CancellationToken);

        await using var services = DemoServices.Build(database.ConnectionString, database.Paths);
        await using var scope = services.CreateAsyncScope();
        var balances = await scope.ServiceProvider.GetRequiredService<IBalanceReadModel>()
            .BalancesAsync(TestContext.Current.CancellationToken);

        balances.ToDictionary(wallet => wallet.WalletName, wallet => wallet.Balances).Should().BeEquivalentTo(
            new Dictionary<string, IReadOnlyList<Money>>
            {
                // 3000 - 34.50 - 3.20 + 2800 - 42.00 = 5720.30, re-anchored to 5700.00 on 15.09, then - 200 (to Cash EUR) - 40.30
                ["Wise"] = [new(5459.70m, CurrencyCode.Eur)],
                // 180000 - 3450 - 850 - 1039 (pharmacy receipt) - 1200 - 1364.94 (Maxi receipt) - 10150 (withdrawal, fee 150 included)
                ["Raiffeisen"] = [new(161946.06m, CurrencyCode.Rsd)],
                // 600 - 4.50 - 45 + 450
                ["Cash"] = [new(1000.50m, CurrencyCode.Usd)],
                // 50000 - 599 - 1450
                ["Tinkoff"] = [new(47951.00m, CurrencyCode.Rub)],
                // 200000 - 6000 - 8500 - 15600 (30 USD charged at 520) - 156 (its 1 % fee)
                ["Kaspi"] = [new(169744.00m, CurrencyCode.Kzt)],
                ["Old Revolut"] = [new(900.00m, CurrencyCode.Eur)],
                // 15000 opening + 10000 (withdrawal) + 11700 (exchange)
                ["Cash RSD"] = [new(36700.00m, CurrencyCode.Rsd)],
                // 250 opening - 100 (exchange) + 200 (from Wise)
                ["Cash EUR"] = [new(350.00m, CurrencyCode.Eur)],
                ["Main Wallet"] = [],
            });
    }

    [Fact]
    public async Task No_two_transactions_share_a_moment_so_every_list_orders_them_the_same_way_each_run()
    {
        if (database.Unavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await Refresh.RunAsync(database.Admin, database.Name, database.Paths, TestContext.Current.CancellationToken);

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) - count(DISTINCT occurred_at) FROM transactions", connection);

        (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)).Should().Be(0L);
    }

    [Fact]
    public async Task Raiffeisen_takes_the_rsd_default_and_old_revolut_is_archived()
    {
        if (database.Unavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await Refresh.RunAsync(database.Admin, database.Name, database.Paths, TestContext.Current.CancellationToken);

        await using var services = DemoServices.Build(database.ConnectionString, database.Paths);
        await using var scope = services.CreateAsyncScope();
        var wallets = await scope.ServiceProvider.GetRequiredService<IWalletAdmin>()
            .ListAsync(TestContext.Current.CancellationToken);

        wallets.Single(wallet => wallet.Name == "Raiffeisen").IsDefaultForCurrency.Should().BeTrue();
        wallets.Single(wallet => wallet.Name == "Main Wallet").IsDefaultForCurrency.Should().BeFalse();
        wallets.Single(wallet => wallet.Name == "Old Revolut").Archived.Should().BeTrue();
        wallets.Single(wallet => wallet.Name == "Raiffeisen").DefaultForPayment.Should().Be(PaymentMethod.Card);
        wallets.Single(wallet => wallet.Name == "Cash").DefaultForPayment.Should().Be(PaymentMethod.Cash);
    }

    [Fact]
    public async Task Transfers_stay_out_of_this_months_spending_their_fees_count_and_the_exchange_traces_as_a_transfer()
    {
        if (database.Unavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var cancellationToken = TestContext.Current.CancellationToken;
        await Refresh.RunAsync(database.Admin, database.Name, database.Paths, cancellationToken);

        await using var services = DemoServices.Build(database.ConnectionString, database.Paths);
        await using var scope = services.CreateAsyncScope();
        var readModel = scope.ServiceProvider.GetRequiredService<ISpendingReadModel>();
        var month = await readModel.ThisMonthAsync(cancellationToken);
        var transfers = await readModel.TransfersThisMonthAsync(cancellationToken);
        var exchange = (await scope.ServiceProvider.GetRequiredService<ITransactionTrace>()
            .GetAsync(MockData.ExchangeTransactionId, cancellationToken)).Summary!;

        transfers.Select(transfer => transfer.Id).Should().Contain(
            new[] { MockData.WithdrawalTransactionId, MockData.ExchangeTransactionId, MockData.TransferTransactionId });
        month.Totals.Should().Contain(new MonthTotal("Fees & Charges", CurrencyCode.Rsd, 150.00m));
        month.Totals.Should().Contain(new MonthTotal("Fees & Charges", CurrencyCode.Kzt, 156.00m));
        month.Totals.Should().Contain(new MonthTotal("Subscriptions", CurrencyCode.Usd, 30.00m),
            "a foreign spending counts in the currency it was bought in (spec §4)");
        exchange.Transfer!.Line.Should().Be(new TransferLine(
            "Cash EUR", new Money(100.00m, CurrencyCode.Eur), "Cash RSD", new Money(11700.00m, CurrencyCode.Rsd),
            null, null, new ExchangeRate(CurrencyCode.Eur, 117m, CurrencyCode.Rsd)));
        exchange.Transfer.RateStated.Should().BeFalse();
    }

    [Fact]
    public async Task Each_cash_wallet_is_the_cash_default_for_its_own_currency()
    {
        if (database.Unavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await Refresh.RunAsync(database.Admin, database.Name, database.Paths, TestContext.Current.CancellationToken);

        await using var services = DemoServices.Build(database.ConnectionString, database.Paths);
        await using var scope = services.CreateAsyncScope();
        var wallets = await scope.ServiceProvider.GetRequiredService<IWalletAdmin>()
            .ListAsync(TestContext.Current.CancellationToken);

        wallets.Where(wallet => wallet.DefaultForPayment == PaymentMethod.Cash)
            .Select(wallet => (wallet.Name, wallet.Currency))
            .Should().BeEquivalentTo(new[] { ("Cash", CurrencyCode.Usd), ("Cash RSD", CurrencyCode.Rsd), ("Cash EUR", CurrencyCode.Eur) });
    }

    [Fact]
    public async Task Kaspi_and_Raiffeisen_carry_their_foreign_currency_terms()
    {
        if (database.Unavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var cancellationToken = TestContext.Current.CancellationToken;
        await Refresh.RunAsync(database.Admin, database.Name, database.Paths, cancellationToken);

        await using var services = DemoServices.Build(database.ConnectionString, database.Paths);
        await using var scope = services.CreateAsyncScope();
        var wallets = await scope.ServiceProvider.GetRequiredService<IWalletAdmin>().ListAsync(cancellationToken);
        var terms = scope.ServiceProvider.GetRequiredService<IWalletFxTerms>();

        (await terms.ListAsync(wallets.Single(wallet => wallet.Name == "Kaspi").Id, cancellationToken)).Should().Equal(
            new WalletTermsDetails(CurrencyCode.Usd, 520m, 1m, null, null));
        (await terms.ListAsync(wallets.Single(wallet => wallet.Name == "Raiffeisen").Id, cancellationToken)).Should().Equal(
            new WalletTermsDetails(CurrencyCode.Eur, 117.35m, 0.5m, null, 100.00m));
    }

    [Fact]
    public async Task Only_the_receipt_that_does_not_add_up_waits_for_record_anyway_and_every_receipt_trace_shows_its_lines()
    {
        if (database.Unavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await Refresh.RunAsync(database.Admin, database.Name, database.Paths, TestContext.Current.CancellationToken);

        await using var services = DemoServices.Build(database.ConnectionString, database.Paths);
        await using var scope = services.CreateAsyncScope();
        var trace = scope.ServiceProvider.GetRequiredService<ITransactionTrace>();

        foreach (var record in MockData.Records.Where(record => record.Receipt is { Kind: ReceiptKind.Sale }))
        {
            var receipt = (await trace.GetAsync(record.Id!.Value, TestContext.Current.CancellationToken)).Receipt!;

            receipt.Lines.Select(line => line.Name).Should().Equal(record.Receipt!.Lines.Select(line => line.Name));
            receipt.AwaitingConfirmation.Should().Be(record.Status == TransactionStatus.Captured, record.Receipt.SellerName);
        }
    }

    [Fact]
    public async Task The_held_slip_waits_for_record_anyway_and_its_trace_names_the_one_problem()
    {
        if (database.Unavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await Refresh.RunAsync(database.Admin, database.Name, database.Paths, TestContext.Current.CancellationToken);

        await using var services = DemoServices.Build(database.ConnectionString, database.Paths);
        await using var scope = services.CreateAsyncScope();
        var receipt = (await scope.ServiceProvider.GetRequiredService<ITransactionTrace>()
            .GetAsync(MockData.HeldSlipTransactionId, TestContext.Current.CancellationToken)).Receipt!;

        receipt.AwaitingConfirmation.Should().BeTrue();
        receipt.Problems.Should().Equal("The given and received amounts don't match the printed rate");
    }

    [Fact]
    public async Task The_retention_the_demo_saves_keeps_every_mock_log_row()
    {
        if (database.Unavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await Refresh.RunAsync(database.Admin, database.Name, database.Paths, TestContext.Current.CancellationToken);

        await using var services = DemoServices.Build(database.ConnectionString, database.Paths);
        await using var scope = services.CreateAsyncScope();

        (await scope.ServiceProvider.GetRequiredService<ILogRetentionSettings>().GetAsync(TestContext.Current.CancellationToken))
            .Should().Be(MockData.LogRetention);
    }

    // The demo host runs on the real clock, and the checks measure idle time on the clock they are given - so this test
    // passes the real one. The only finding is the seeded failed exchange; no Bug (spec P-19).
    [Fact]
    public async Task The_mock_ledger_has_exactly_the_seeded_findings()
    {
        if (database.Unavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var cancellationToken = TestContext.Current.CancellationToken;
        await Refresh.RunAsync(database.Admin, database.Name, database.Paths, cancellationToken);

        await using var services = DemoServices.Build(database.ConnectionString, database.Paths, clock: TimeProvider.System);
        await using var scope = services.CreateAsyncScope();
        var findings = await scope.ServiceProvider.GetRequiredService<IIntegrityChecks>().FindAllAsync(cancellationToken);

        var finding = findings.Should().ContainSingle().Which;
        finding.Check.Should().Be(IntegrityCheck.NotApplied);
        finding.Group.Should().Be(IntegrityGroup.WaitingOnYou);
        finding.TransactionId.Should().Be(MockData.WaitingTransactionId);
        finding.Facts.Should().Contain(new TextFact("Waiting for", "A reply to the echo"));
        finding.Facts.Should().Contain(new TextFact("Reason", "MissingReceivedAmount"));
    }

    // The failed record, the held receipt and the held slip are dated in the mock month; stamped with the real now at
    // seeding, they never read as idle over a day, whichever day the pictures are taken.
    [Fact]
    public async Task The_month_dated_records_that_could_wait_on_the_operator_were_last_active_at_seeding()
    {
        if (database.Unavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var cancellationToken = TestContext.Current.CancellationToken;
        await Refresh.RunAsync(database.Admin, database.Name, database.Paths, cancellationToken);

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT transaction_id, max(updated_at) FROM categorization_jobs WHERE transaction_id = ANY(@ids) GROUP BY transaction_id",
            connection);
        command.Parameters.AddWithValue(
            "ids", new[] { MockData.FailedTransactionId, MockData.UnconfirmedReceiptTransactionId, MockData.HeldSlipTransactionId });

        var lastActive = new Dictionary<Guid, DateTimeOffset>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                lastActive[reader.GetGuid(0)] = reader.GetFieldValue<DateTimeOffset>(1);
        }

        lastActive.Keys.Should().BeEquivalentTo(
            new[] { MockData.FailedTransactionId, MockData.UnconfirmedReceiptTransactionId, MockData.HeldSlipTransactionId });
        lastActive.Values.Should().AllSatisfy(at => at.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10)));
    }

    // Inserted by column name with literal JSON, so the store reading them back is what proves the shape.
    [Fact]
    public async Task The_demo_bug_reports_read_back_as_filed_and_the_dashboard_one_has_a_finding_then_and_none_now()
    {
        if (database.Unavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var cancellationToken = TestContext.Current.CancellationToken;
        await Refresh.RunAsync(database.Admin, database.Name, database.Paths, cancellationToken);

        await using var services = DemoServices.Build(database.ConnectionString, database.Paths, clock: TimeProvider.System);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IBugReportStore>();

        (await store.ListAsync(includeClosed: true, cancellationToken))
            .Select(report => (report.Number, report.Source, report.Status, report.ExplanationState))
            .Should().Equal(
                (3, BugReportSource.Telegram, BugReportStatus.Closed, BugExplanationState.Failed),
                (2, BugReportSource.Dashboard, BugReportStatus.Open, BugExplanationState.Done),
                (1, BugReportSource.Telegram, BugReportStatus.Open, BugExplanationState.Done));
        (await store.CountOpenAsync(cancellationToken)).Should().Be(2);

        var dashboard = (await store.GetAsync(2, cancellationToken))!;
        dashboard.FindingsThen.Should().ContainSingle().Which.Facts
            .Should().Contain(new TextFact("Condition", "Failure reason on a record that is not failed"));
        dashboard.FindingsNow.Should().BeEmpty("the traced record is consistent today");
        dashboard.LooksLikeBug.Should().BeTrue();
        dashboard.LogLines.Should().HaveCount(3);
        dashboard.Revisions.Should().HaveCount(2, "the traced record's Initial and Correction, read live");
        dashboard.RecordSummary.Should().ContainAll(
            dashboard.Revisions.Select(revision =>
                $"- {revision.At.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC · {revision.ChangeKind} · {revision.Details}"),
            "the record as filed dates its revisions as the record's own trace does");

        var exchange = (await store.GetAsync(1, cancellationToken))!;
        exchange.FindingsThen.Should().ContainSingle().Which.TransactionId.Should().Be(MockData.WaitingTransactionId);
        exchange.FindingsNow.Should().ContainSingle("the exchange still waits on the operator");
        exchange.LooksLikeBug.Should().BeFalse();

        var closed = (await store.GetAsync(3, cancellationToken))!;
        closed.FindingsThen.Should().BeNull();
        closed.CollectionFailures.Should().Be("findings: check failed (TimeoutException)");
        closed.LogLines.Should().HaveCount(2);
    }
}
