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

    async Task<Guid> SeedWalletAsync(string name)
    {
        var walletId = Guid.NewGuid();
        await using var db = OpenDb();
        db.Wallets.Add(new Wallet
        {
            Id = walletId,
            Name = name,
            Currency = CurrencyCode.Eur,
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
