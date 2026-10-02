using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence;

namespace Noof.Ledger.E2E.Tests;

public sealed class TransactionsTests(CookieModeHostFixture fixture) : PageTest, IClassFixture<CookieModeHostFixture>
{
    [Fact]
    public async Task The_grid_shows_seeded_transactions_of_every_kind_newest_first_and_links_to_their_trace()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var marker = Guid.NewGuid().ToString("N")[..8];
        var walletId = await SeedWalletAsync($"Grid wallet {marker}");
        var now = DateTimeOffset.UtcNow;

        var expenseId = Guid.NewGuid();
        var incomeId = Guid.NewGuid();
        var statementId = Guid.NewGuid();

        await using (var db = OpenDb())
        {
            db.Transactions.Add(NewTransaction(expenseId, walletId, TransactionKind.Expense, TransactionStatus.Completed,
                $"grid expense {marker}", now.AddMinutes(-2)));
            db.Transactions.Add(NewTransaction(incomeId, walletId, TransactionKind.Income, TransactionStatus.Completed,
                $"grid income {marker}", now.AddMinutes(-1)));
            db.Transactions.Add(NewTransaction(statementId, walletId, TransactionKind.BalanceCheck, TransactionStatus.Completed,
                $"grid statement {marker}", now));
            db.BalanceChecks.Add(new BalanceCheck
            {
                TransactionId = statementId,
                WalletId = walletId,
                Stated = new Money(1000m, CurrencyCode.Eur),
                ComputedBefore = 950m,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/transactions");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var grid = Page.Locator("#transactions-grid");
        await Expect(grid).ToBeVisibleAsync();
        await Expect(grid).ToContainTextAsync($"grid expense {marker}");
        await Expect(grid).ToContainTextAsync($"grid income {marker}");
        await Expect(grid).ToContainTextAsync($"grid statement {marker}");
        await Expect(grid).ToContainTextAsync("1,000.00 EUR");

        var rows = grid.Locator("tbody tr");
        var rowTexts = (await rows.AllTextContentsAsync()).ToList();
        var statementIndex = rowTexts.FindIndex(t => t.Contains($"grid statement {marker}", StringComparison.Ordinal));
        var incomeIndex = rowTexts.FindIndex(t => t.Contains($"grid income {marker}", StringComparison.Ordinal));
        var expenseIndex = rowTexts.FindIndex(t => t.Contains($"grid expense {marker}", StringComparison.Ordinal));
        Assert.True(statementIndex >= 0 && incomeIndex >= 0 && expenseIndex >= 0
            && statementIndex < incomeIndex && incomeIndex < expenseIndex,
            "rows must render newest-occurred first: statement, then income, then expense");

        var incomeRow = grid.Locator("tr", new LocatorLocatorOptions { HasText = $"grid income {marker}" });
        await incomeRow.Locator("a").First.ClickAsync();
        await Page.WaitForURLAsync($"**/transactions/{incomeId}/trace");
    }

    [Fact]
    public async Task A_text_filter_narrows_the_grid_to_matching_rows()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var marker = Guid.NewGuid().ToString("N")[..8];
        var walletId = await SeedWalletAsync($"Filter wallet {marker}");
        var now = DateTimeOffset.UtcNow;

        await using (var db = OpenDb())
        {
            db.Transactions.Add(NewTransaction(Guid.NewGuid(), walletId, TransactionKind.Expense, TransactionStatus.Completed,
                $"filter match {marker}", now));
            db.Transactions.Add(NewTransaction(Guid.NewGuid(), walletId, TransactionKind.Expense, TransactionStatus.Completed,
                "unrelated row that must not match", now));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/transactions");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var grid = Page.Locator("#transactions-grid");
        await Expect(grid).ToBeVisibleAsync();

        await Page.FillAsync("#transactions-filter-text", marker);
        await Expect(grid).ToContainTextAsync($"filter match {marker}");
        await Expect(grid).Not.ToContainTextAsync("unrelated row that must not match");
    }

    [Fact]
    public async Task Changing_two_filters_in_quick_succession_still_narrows_the_grid_without_crashing_the_circuit()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var marker = Guid.NewGuid().ToString("N")[..8];
        var walletA = await SeedWalletAsync($"Race wallet A {marker}");
        var walletB = await SeedWalletAsync($"Race wallet B {marker}");
        var now = DateTimeOffset.UtcNow;

        await using (var db = OpenDb())
        {
            db.Transactions.Add(NewTransaction(Guid.NewGuid(), walletA, TransactionKind.Expense, TransactionStatus.Completed,
                $"race a expense {marker}", now));
            db.Transactions.Add(NewTransaction(Guid.NewGuid(), walletA, TransactionKind.Income, TransactionStatus.Completed,
                $"race a income {marker}", now));
            db.Transactions.Add(NewTransaction(Guid.NewGuid(), walletB, TransactionKind.Expense, TransactionStatus.Completed,
                $"race b expense {marker}", now));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/transactions");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var grid = Page.Locator("#transactions-grid");
        await Expect(grid).ToBeVisibleAsync();
        await Expect(grid).ToContainTextAsync($"race a expense {marker}");
        await Expect(grid).ToContainTextAsync($"race a income {marker}");
        await Expect(grid).ToContainTextAsync($"race b expense {marker}");

        // Two filter changes fired back to back, deliberately not waiting for the first round-trip
        // to land - the sequence the Task 9 fix round found most likely to race a QuickGrid rebuild
        // against a still in-flight ItemsProvider call on the circuit's one scoped DbContext.
        await Page.SelectOptionAsync("#transactions-filter-wallet", walletA.ToString());
        await Page.SelectOptionAsync("#transactions-filter-kind", nameof(TransactionKind.Expense));
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Expect(grid).ToContainTextAsync($"race a expense {marker}", new() { Timeout = 10_000 });
        await Expect(grid).Not.ToContainTextAsync($"race a income {marker}");
        await Expect(grid).Not.ToContainTextAsync($"race b expense {marker}");

        fixture.CapturedOutputLines.Should().NotContain(
            line => line.Contains("second operation was started on this context", StringComparison.OrdinalIgnoreCase),
            "a rapid filter change must never race two queries against the circuit's one DbContext");
    }

    // M-4 (Phase 5 final review): the Today/7 days/This month buttons used to compute "today" from
    // DateTime.Today - the server's own zone, not the capture zone (OccurredOn is a local date in
    // Capture:TimeZone) - and bypassed TimeProvider. No test exercised these buttons at all before
    // this one; it cannot pin the zone difference without a fake clock reachable from a real browser
    // circuit (the review's own limitation), but it is real regression coverage for a page control
    // that previously had none.
    [Fact]
    public async Task The_Today_quick_range_narrows_to_todays_transaction()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var marker = Guid.NewGuid().ToString("N")[..8];
        var walletId = await SeedWalletAsync($"Quick range wallet {marker}");
        var now = DateTimeOffset.UtcNow;

        await using (var db = OpenDb())
        {
            db.Transactions.Add(NewTransaction(Guid.NewGuid(), walletId, TransactionKind.Expense, TransactionStatus.Completed,
                $"today row {marker}", now));
            db.Transactions.Add(NewTransaction(Guid.NewGuid(), walletId, TransactionKind.Expense, TransactionStatus.Completed,
                $"two days ago row {marker}", now.AddDays(-2)));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/transactions");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var grid = Page.Locator("#transactions-grid");
        await Expect(grid).ToBeVisibleAsync();
        await Expect(grid).ToContainTextAsync($"today row {marker}");
        await Expect(grid).ToContainTextAsync($"two days ago row {marker}");

        await Page.GetByRole(AriaRole.Button, new() { Name = "Today", Exact = true }).ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Expect(grid).ToContainTextAsync($"today row {marker}", new() { Timeout = 10_000 });
        await Expect(grid).Not.ToContainTextAsync($"two days ago row {marker}");
    }

    [Fact]
    public async Task The_receipts_only_filter_shows_only_transactions_with_a_receipt_and_marks_the_shop()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var marker = Guid.NewGuid().ToString("N")[..8];
        var walletId = await SeedWalletAsync($"Receipt filter wallet {marker}");
        var now = DateTimeOffset.UtcNow;
        var receiptTransactionId = Guid.NewGuid();
        var plainTransactionId = Guid.NewGuid();

        await using (var db = OpenDb())
        {
            db.Transactions.Add(NewTransaction(receiptTransactionId, walletId, TransactionKind.Expense, TransactionStatus.Completed,
                $"receipt row {marker}", now));
            db.Transactions.Add(NewTransaction(plainTransactionId, walletId, TransactionKind.Expense, TransactionStatus.Completed,
                $"plain row {marker}", now));
            db.Receipts.Add(new Receipt
            {
                Id = Guid.NewGuid(),
                TransactionId = receiptTransactionId,
                Source = ReceiptSource.Vision,
                SellerName = $"Shop {marker}",
                Total = new Money(300m, CurrencyCode.Eur),
                Kind = ReceiptKind.Sale,
                CreatedAt = now,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/transactions");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var grid = Page.Locator("#transactions-grid");
        await Expect(grid).ToContainTextAsync($"receipt row {marker}");
        await Expect(grid).ToContainTextAsync($"Shop {marker}");

        await Page.GetByRole(AriaRole.Checkbox, new() { Name = "Receipts only" }).ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Expect(grid).ToContainTextAsync($"receipt row {marker}", new() { Timeout = 10_000 });
        await Expect(grid).Not.ToContainTextAsync($"plain row {marker}");
    }

    [Fact]
    public async Task The_quick_range_buttons_mark_the_active_range_until_a_date_is_edited_by_hand()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/transactions");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var all = Page.GetByRole(AriaRole.Button, new() { Name = "All", Exact = true });
        var today = Page.GetByRole(AriaRole.Button, new() { Name = "Today", Exact = true });

        await Expect(all).ToHaveAttributeAsync("aria-pressed", "true");
        await Expect(today).ToHaveAttributeAsync("aria-pressed", "false");

        await today.ClickAsync();
        await Expect(today).ToHaveAttributeAsync("aria-pressed", "true");
        await Expect(all).ToHaveAttributeAsync("aria-pressed", "false");

        await Page.FillAsync("#transactions-filter-from", "2026-01-01");
        await Expect(today).ToHaveAttributeAsync("aria-pressed", "false");
        await Expect(all).ToHaveAttributeAsync("aria-pressed", "false");
    }

    [Fact]
    public async Task A_dashboard_recent_row_links_to_the_trace_page()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var marker = Guid.NewGuid().ToString("N")[..8];
        var walletId = await SeedWalletAsync($"Dashboard wallet {marker}");
        var transactionId = Guid.NewGuid();

        await using (var db = OpenDb())
        {
            db.Transactions.Add(NewTransaction(transactionId, walletId, TransactionKind.Expense, TransactionStatus.Completed,
                $"dashboard recent {marker}", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Page.Locator($"#txn-{transactionId} a").First.ClickAsync();
        await Page.WaitForURLAsync($"**/transactions/{transactionId}/trace");
    }

    [Fact]
    public async Task The_spending_preset_hides_transfers_and_a_transfer_row_shows_what_each_wallet_moved()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var marker = Guid.NewGuid().ToString("N")[..8];
        var euro = await SeedWalletAsync($"Preset EUR {marker}");
        var dinar = await SeedWalletAsync($"Preset RSD {marker}", CurrencyCode.Rsd);
        var now = DateTimeOffset.UtcNow;

        await using (var db = OpenDb())
        {
            db.Transactions.Add(NewTransaction(Guid.NewGuid(), euro, TransactionKind.Expense, TransactionStatus.Completed,
                $"preset expense {marker}", now.AddMinutes(-1)));
            AddTransfer(db, Guid.NewGuid(), euro, new Money(100.00m, CurrencyCode.Eur), dinar, new Money(11700.00m, CurrencyCode.Rsd),
                $"preset exchange {marker}", now);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/transactions");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var grid = Page.Locator("#transactions-grid");
        var exchangeRow = grid.Locator("tr", new LocatorLocatorOptions { HasText = $"preset exchange {marker}" });
        await Expect(exchangeRow).ToContainTextAsync("-100.00 EUR → +11,700.00 RSD");
        await Expect(exchangeRow).ToContainTextAsync($"Preset EUR {marker} → Preset RSD {marker}");

        await Page.GotoAsync(fixture.BaseUrl + "/transactions?view=spending");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(grid).ToContainTextAsync($"preset expense {marker}");
        await Expect(grid).Not.ToContainTextAsync($"preset exchange {marker}");

        // In-app navigation keeps this page's component and circuit, so only OnParametersSet sees the new ?view=.
        await Page.EvaluateAsync("() => Blazor.navigateTo('/transactions?view=transfers')");
        await Expect(Page).ToHaveURLAsync(new Regex(@"/transactions\?view=transfers$"));
        await Expect(grid).Not.ToContainTextAsync($"preset expense {marker}", new() { Timeout = 10_000 });
        await Expect(grid).ToContainTextAsync($"preset exchange {marker}");
    }

    [Fact]
    public async Task Filtering_by_the_wallet_a_transfer_went_to_finds_the_transfer()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var marker = Guid.NewGuid().ToString("N")[..8];
        var source = await SeedWalletAsync($"Leg source {marker}");
        var destination = await SeedWalletAsync($"Leg destination {marker}", CurrencyCode.Rsd);
        var now = DateTimeOffset.UtcNow;

        await using (var db = OpenDb())
        {
            db.Transactions.Add(NewTransaction(Guid.NewGuid(), source, TransactionKind.Expense, TransactionStatus.Completed,
                $"leg expense {marker}", now.AddMinutes(-1)));
            AddTransfer(db, Guid.NewGuid(), source, new Money(50.00m, CurrencyCode.Eur), destination, new Money(5850.00m, CurrencyCode.Rsd),
                $"leg exchange {marker}", now);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/transactions");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var grid = Page.Locator("#transactions-grid");
        await Expect(grid).ToContainTextAsync($"leg expense {marker}");

        await Page.SelectOptionAsync("#transactions-filter-wallet", destination.ToString());
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // The absence first: it waits out the reload, so the presence below is read from the filtered grid, not the
        // unfiltered one that also held the transfer.
        await Expect(grid).Not.ToContainTextAsync($"leg expense {marker}", new() { Timeout = 10_000 });
        await Expect(grid).ToContainTextAsync($"leg exchange {marker}");
    }

    async Task<Guid> SeedWalletAsync(string name, CurrencyCode? currency = null)
    {
        var walletId = Guid.NewGuid();
        await using var db = OpenDb();
        db.Wallets.Add(new Wallet
        {
            Id = walletId,
            Name = name,
            Currency = currency ?? CurrencyCode.Eur,
            Aliases = [],
            IsDefaultForCurrency = false,
            Archived = false,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return walletId;
    }

    static Transaction NewTransaction(
        Guid id, Guid walletId, TransactionKind kind, TransactionStatus status, string rawText, DateTimeOffset occurredAt) => new()
    {
        Id = id,
        WalletId = walletId,
        RawText = rawText,
        CaptureKind = CaptureKind.Manual,
        Kind = kind,
        Status = status,
        TimeZoneId = "Europe/Belgrade",
        OccurredAt = occurredAt,
        OccurredOn = ZonedClock.LocalDate(occurredAt, "Europe/Belgrade"),
        TelegramChatId = null,
        TelegramMessageId = null,
        CreatedAt = occurredAt,
    };

    static void AddTransfer(LedgerDbContext db, Guid id, Guid fromWalletId, Money from, Guid toWalletId, Money to, string rawText, DateTimeOffset at)
    {
        db.Transactions.Add(NewTransaction(id, fromWalletId, TransactionKind.Transfer, TransactionStatus.Completed, rawText, at));
        db.Transfers.Add(new Transfer
        {
            TransactionId = id,
            FromWalletId = fromWalletId,
            From = from,
            ToWalletId = toWalletId,
            To = to,
        });
    }

    LedgerDbContext OpenDb() =>
        new(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(fixture.ConnectionString).Options);

    async Task SignInAsync()
    {
        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForURLAsync("**/account/login*");
        await Page.FillAsync("input[name='username']", CookieModeHostFixture.Username);
        await Page.FillAsync("input[name='password']", CookieModeHostFixture.Password);
        await Page.ClickAsync("button[type='submit']");
        await Page.WaitForURLAsync(fixture.BaseUrl + "/");
    }
}
