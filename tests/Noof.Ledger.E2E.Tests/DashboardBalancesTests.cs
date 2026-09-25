using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence;

namespace Noof.Ledger.E2E.Tests;

public sealed class DashboardBalancesTests(CookieModeHostFixture fixture) : PageTest, IClassFixture<CookieModeHostFixture>
{
    [Fact]
    public async Task The_balances_card_shows_a_wallets_balance_and_the_per_currency_total()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var walletId = Guid.NewGuid();
        var walletName = $"Wise EUR {Guid.NewGuid():N}";
        var transactionId = Guid.NewGuid();

        await using (var db = OpenDb())
        {
            db.Wallets.Add(new Wallet
            {
                Id = walletId,
                Name = walletName,
                Currency = CurrencyCode.Eur,
                Aliases = [],
                IsDefaultForCurrency = false,
                Archived = false,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Transactions.Add(new Transaction
            {
                Id = transactionId,
                WalletId = walletId,
                RawText = "Opening balance",
                CaptureKind = CaptureKind.Manual,
                Kind = TransactionKind.BalanceCheck,
                Status = TransactionStatus.Completed,
                TimeZoneId = "Europe/Belgrade",
                OccurredAt = DateTimeOffset.UtcNow,
                OccurredOn = ZonedClock.LocalDate(DateTimeOffset.UtcNow, "Europe/Belgrade"),
                TelegramChatId = null,
                TelegramMessageId = null,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.BalanceChecks.Add(new BalanceCheck
            {
                TransactionId = transactionId,
                WalletId = walletId,
                Stated = new Money(3200.00m, CurrencyCode.Eur),
                ComputedBefore = 0m,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var card = Page.Locator("#balances");
        await Expect(card).ToBeVisibleAsync();

        var row = Page.Locator($"#balance-{walletId}");
        await Expect(row).ToContainTextAsync(walletName);
        // FormatAmount uses "N2" + InvariantCulture (Global Constraints), which groups thousands.
        await Expect(row).ToContainTextAsync("3,200.00 EUR");

        var total = Page.Locator("#balance-total-EUR");
        await Expect(total).ToContainTextAsync("3,200.00");
    }

    [Fact]
    public async Task An_archived_wallet_is_left_out_of_the_balances_card_and_its_total()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var walletId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();

        await using (var db = OpenDb())
        {
            db.Wallets.Add(new Wallet
            {
                Id = walletId,
                Name = $"Retired KZT {Guid.NewGuid():N}",
                Currency = CurrencyCode.Kzt,
                Aliases = [],
                IsDefaultForCurrency = false,
                Archived = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Transactions.Add(new Transaction
            {
                Id = transactionId,
                WalletId = walletId,
                RawText = "Opening balance",
                CaptureKind = CaptureKind.Manual,
                Kind = TransactionKind.BalanceCheck,
                Status = TransactionStatus.Completed,
                TimeZoneId = "Europe/Belgrade",
                OccurredAt = DateTimeOffset.UtcNow,
                OccurredOn = ZonedClock.LocalDate(DateTimeOffset.UtcNow, "Europe/Belgrade"),
                TelegramChatId = null,
                TelegramMessageId = null,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.BalanceChecks.Add(new BalanceCheck
            {
                TransactionId = transactionId,
                WalletId = walletId,
                Stated = new Money(500000m, CurrencyCode.Kzt),
                ComputedBefore = 0m,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Expect(Page.Locator($"#balance-{walletId}")).Not.ToBeVisibleAsync();
        await Expect(Page.Locator("#balance-total-KZT")).Not.ToBeVisibleAsync();
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
