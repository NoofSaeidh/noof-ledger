using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence;
using Noof.Ledger.TestKit;

namespace Noof.Ledger.E2E.Tests;

public sealed class MoneyExactnessDashboardTests(CookieModeHostFixture fixture) : PageTest, IClassFixture<CookieModeHostFixture>
{
    const string TimeZone = "Europe/Belgrade";
    static readonly DateOnly Day1 = new(2026, 9, 1);
    static readonly DateTimeOffset At1 = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("ru-RU")]
    [InlineData("sr-Latn-RS")]
    public async Task The_dashboard_serves_exact_invariant_formatted_balances(string cultureName)
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        // The host process itself always formats with InvariantCulture (CLAUDE.md §4), so this
        // scope's job is the same one it has in MoneyExactnessTests: catching a test-side
        // comparison that forgot to be culture-explicit, not simulating a Serbian Windows install.
        using var culture = new CultureScope(cultureName);

        // Fresh ids per theory invocation, not static fields: both InlineData rows share this
        // class's one CookieModeHostFixture database, so a fixed id would collide with the row
        // that ran first (PK_wallets) the moment a second culture is added.
        var eurWalletId = Guid.NewGuid();
        var rsdWalletId = Guid.NewGuid();

        await using (var db = OpenDb())
        {
            db.Wallets.AddRange(
                new Wallet { Id = eurWalletId, Name = $"Wise EUR {cultureName}", Currency = CurrencyCode.Eur, Aliases = [], IsDefaultForCurrency = false, Archived = false, CreatedAt = At1 },
                new Wallet { Id = rsdWalletId, Name = $"Raiffeisen RSD {cultureName}", Currency = CurrencyCode.Rsd, Aliases = [], IsDefaultForCurrency = false, Archived = false, CreatedAt = At1 });

            AddCheckpoint(db, eurWalletId, CurrencyCode.Eur, 1000.00m, 0m);
            AddEntry(db, eurWalletId, CurrencyCode.Eur, -0.10m, TransactionKind.Expense);
            AddEntry(db, eurWalletId, CurrencyCode.Eur, -0.20m, TransactionKind.Expense);

            AddCheckpoint(db, rsdWalletId, CurrencyCode.Rsd, 44_800.00m, 0m);

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Expect(Page.Locator($"#balance-{eurWalletId}")).ToContainTextAsync("999.70 EUR");
        await Expect(Page.Locator($"#balance-{rsdWalletId}")).ToContainTextAsync("44,800.00 RSD");
    }

    void AddCheckpoint(LedgerDbContext db, Guid walletId, CurrencyCode currency, decimal amount, decimal computedBefore)
    {
        var transactionId = Guid.NewGuid();
        db.Transactions.Add(new Transaction
        {
            Id = transactionId, WalletId = walletId, Kind = TransactionKind.BalanceCheck, CaptureKind = CaptureKind.Manual,
            RawText = "Opening balance", Status = TransactionStatus.Completed, TimeZoneId = TimeZone,
            OccurredAt = At1, OccurredOn = Day1, TelegramChatId = null, TelegramMessageId = null, CreatedAt = At1,
        });
        db.BalanceChecks.Add(new BalanceCheck { TransactionId = transactionId, WalletId = walletId, Stated = new Money(amount, currency), ComputedBefore = computedBefore });
    }

    void AddEntry(LedgerDbContext db, Guid walletId, CurrencyCode currency, decimal signedAmount, TransactionKind kind)
    {
        var transactionId = Guid.NewGuid();
        db.Transactions.Add(new Transaction
        {
            Id = transactionId, WalletId = walletId, Kind = kind, CaptureKind = CaptureKind.Manual,
            RawText = "seeded", Status = TransactionStatus.Completed, TimeZoneId = TimeZone,
            OccurredAt = At1.AddDays(1), OccurredOn = Day1.AddDays(1), TelegramChatId = null, TelegramMessageId = null, CreatedAt = At1.AddDays(1),
        });
        db.Entries.Add(new Entry { Id = Guid.NewGuid(), TransactionId = transactionId, WalletId = walletId, Amount = new Money(signedAmount, currency), Role = EntryRole.Principal });
    }

    LedgerDbContext OpenDb() => new(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(fixture.ConnectionString).Options);

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
