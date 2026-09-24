using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence;
using Noof.Ledger.Persistence.Backup;

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

    [Fact]
    public async Task Backup_status_says_never_run_when_no_backup_has_ever_completed()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        // The three backup-status tests share one clone database and xunit does not guarantee their
        // order, so each one clears backup_runs first rather than assuming it starts empty.
        await using (var db = OpenDb())
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM backup_runs", TestContext.Current.CancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Expect(Page.Locator("#backup-status")).ToContainTextAsync("Last backup: never");
    }

    [Fact]
    public async Task Backup_status_reports_how_long_ago_the_last_success_was()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var startedAt = DateTimeOffset.UtcNow.AddHours(-3).AddMinutes(-5);
        var finishedAt = DateTimeOffset.UtcNow.AddHours(-3);

        await using (var db = OpenDb())
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM backup_runs", TestContext.Current.CancellationToken);
            db.BackupRuns.Add(new BackupRun
            {
                Id = Guid.NewGuid(),
                StartedAt = startedAt,
                FinishedAt = finishedAt,
                Succeeded = true,
                FileName = "noof_ledger-20260921-030000.dump",
                SizeBytes = 12_345,
                Error = null,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var status = Page.Locator("#backup-status");
        await Expect(status).ToContainTextAsync("Last backup: 3 h ago");
        // 3 hours is under B3's 36-hour staleness threshold - the text stays the default colour.
        await Expect(status).Not.ToHaveClassAsync(new Regex("mud-warning-text"));
    }

    [Fact]
    public async Task Backup_status_reports_a_failed_run_and_when_the_last_success_was()
    {
        // M-5 (Phase 4 final review): "Last backup: failed" alone hid whether an earlier backup
        // still exists at all - the operator could not tell "yesterday's dump is fine, tonight's
        // just failed" from "nothing has ever worked" without a database connection.
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var oldSuccess = DateTimeOffset.UtcNow.AddHours(-3);
        var recentFailure = DateTimeOffset.UtcNow.AddMinutes(-10);

        await using (var db = OpenDb())
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM backup_runs", TestContext.Current.CancellationToken);
            db.BackupRuns.Add(new BackupRun
            {
                Id = Guid.NewGuid(),
                StartedAt = oldSuccess.AddMinutes(-1),
                FinishedAt = oldSuccess,
                Succeeded = true,
                FileName = "noof_ledger-old.dump",
                SizeBytes = 1000,
                Error = null,
            });
            db.BackupRuns.Add(new BackupRun
            {
                Id = Guid.NewGuid(),
                StartedAt = recentFailure.AddMinutes(-1),
                FinishedAt = recentFailure,
                Succeeded = false,
                FileName = null,
                SizeBytes = null,
                Error = "pg_dump exited with code 1",
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var status = Page.Locator("#backup-status");
        await Expect(status).ToContainTextAsync("Last backup: failed · last success 3 h ago");
        await Expect(status).ToHaveClassAsync(new Regex("mud-warning-text"));
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
