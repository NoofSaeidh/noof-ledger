using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence;
using Noof.Ledger.Persistence.Diagnostics;
using Noof.Ledger.Persistence.Revisions;

namespace Noof.Ledger.E2E.Tests;

public sealed class TransactionTraceTests(CookieModeHostFixture fixture) : PageTest, IClassFixture<CookieModeHostFixture>
{
    [Fact]
    public async Task The_trace_page_shows_stages_a_timeline_and_revision_history()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var transactionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var walletId = await SeedWalletAsync();

        await using (var db = OpenDb())
        {
            db.Transactions.Add(new Transaction
            {
                Id = transactionId,
                WalletId = walletId,
                RawText = "seeded trace transaction",
                CaptureKind = CaptureKind.Manual,
                Kind = TransactionKind.Expense,
                Status = TransactionStatus.Completed,
                TimeZoneId = "Europe/Belgrade",
                OccurredAt = now,
                OccurredOn = ZonedClock.LocalDate(now, "Europe/Belgrade"),
                TelegramChatId = null,
                TelegramMessageId = null,
                CreatedAt = now,
            });

            db.AppLogs.Add(NewStageRow(transactionId, now, TransactionStages.Received, TransactionStages.ReceivedEventId));
            db.AppLogs.Add(NewStageRow(transactionId, now.AddMilliseconds(312), TransactionStages.Categorized, TransactionStages.CategorizedEventId));
            db.AppLogs.Add(NewStageRow(transactionId, now.AddSeconds(1), TransactionStages.Persisted, TransactionStages.PersistedEventId));

            db.TransactionRevisions.Add(new TransactionRevision
            {
                Id = Guid.NewGuid(),
                TransactionId = transactionId,
                RevisionNumber = 1,
                Kind = RevisionKind.Initial,
                Instruction = null,
                StatusBefore = TransactionStatus.Captured,
                StatusAfter = TransactionStatus.Completed,
                Snapshot = "{}",
                CreatedAt = now,
            });

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync($"{fixture.BaseUrl}/transactions/{transactionId}/trace");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var stages = Page.Locator("#trace-stages");
        await Expect(stages).ToContainTextAsync("Received");
        await Expect(stages).ToContainTextAsync("Categorized");
        await Expect(stages).ToContainTextAsync("Persisted");

        var timeline = Page.Locator("#trace-timeline");
        await Expect(timeline).ToContainTextAsync("+0 ms");
        await Expect(timeline).ToContainTextAsync("+312 ms");
        await Expect(timeline).ToContainTextAsync("+1.0 s");

        var history = Page.Locator("#trace-history");
        await Expect(history).ToContainTextAsync("Initial");
    }

    [Fact]
    public async Task A_StageFailed_event_renders_its_failed_stage_chip_red()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var transactionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var walletId = await SeedWalletAsync();

        await using (var db = OpenDb())
        {
            db.Transactions.Add(new Transaction
            {
                Id = transactionId,
                WalletId = walletId,
                RawText = "seeded failed trace transaction",
                CaptureKind = CaptureKind.Manual,
                Kind = TransactionKind.Expense,
                Status = TransactionStatus.Failed,
                TimeZoneId = "Europe/Belgrade",
                OccurredAt = now,
                OccurredOn = ZonedClock.LocalDate(now, "Europe/Belgrade"),
                TelegramChatId = null,
                TelegramMessageId = null,
                CreatedAt = now,
            });

            db.AppLogs.Add(NewStageRow(transactionId, now, TransactionStages.Received, TransactionStages.ReceivedEventId));
            db.AppLogs.Add(NewStageFailedRow(transactionId, now.AddSeconds(1), TransactionStages.Categorized));

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync($"{fixture.BaseUrl}/transactions/{transactionId}/trace");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var failedChip = Page.Locator("#trace-stages .mud-chip", new PageLocatorOptions { HasText = TransactionStages.Categorized });
        await Expect(failedChip).ToHaveClassAsync(new Regex("mud-chip-color-error"));
    }

    [Fact]
    public async Task A_transaction_id_with_no_log_rows_but_a_real_transaction_shows_trace_expired()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var transactionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var walletId = await SeedWalletAsync();

        await using (var db = OpenDb())
        {
            db.Transactions.Add(new Transaction
            {
                Id = transactionId,
                WalletId = walletId,
                RawText = "old transaction, pruned logs",
                CaptureKind = CaptureKind.Manual,
                Kind = TransactionKind.Expense,
                Status = TransactionStatus.Completed,
                TimeZoneId = "Europe/Belgrade",
                OccurredAt = now,
                OccurredOn = ZonedClock.LocalDate(now, "Europe/Belgrade"),
                TelegramChatId = null,
                TelegramMessageId = null,
                CreatedAt = now,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync($"{fixture.BaseUrl}/transactions/{transactionId}/trace");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Expect(Page.Locator("#trace-timeline")).ToContainTextAsync("Trace expired");
    }

    [Fact]
    public async Task An_unknown_transaction_id_shows_a_not_found_state()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await SignInAsync();
        await Page.GotoAsync($"{fixture.BaseUrl}/transactions/{Guid.NewGuid()}/trace");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Expect(Page.Locator("text=No transaction with this id.")).ToBeVisibleAsync();
    }

    async Task<Guid> SeedWalletAsync()
    {
        var walletId = Guid.NewGuid();
        await using var db = OpenDb();
        db.Wallets.Add(new Wallet
        {
            Id = walletId,
            Name = $"Trace wallet {Guid.NewGuid():N}",
            Currency = CurrencyCode.Eur,
            Aliases = [],
            IsDefaultForCurrency = false,
            Archived = false,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return walletId;
    }

    static AppLogEntry NewStageRow(Guid transactionId, DateTimeOffset at, string stage, int eventId) => new()
    {
        Id = 0,
        LoggedAt = at,
        Level = LogSeverity.Information,
        Source = "TransactionTraceTests",
        Message = stage,
        Template = "{Stage}",
        Exception = null,
        TransactionId = transactionId,
        PropertiesJson = $$"""{"Stage":"{{stage}}","EventId":{"Id":{{eventId}},"Name":"{{stage}}"},"TransactionId":"{{transactionId}}"}""",
    };

    static AppLogEntry NewStageFailedRow(Guid transactionId, DateTimeOffset at, string failedStage) => new()
    {
        Id = 0,
        LoggedAt = at,
        Level = LogSeverity.Error,
        Source = "TransactionTraceTests",
        Message = $"{TransactionStages.StageFailed} at stage {failedStage}",
        Template = "{Stage} at stage {FailedStage}",
        Exception = null,
        TransactionId = transactionId,
        PropertiesJson = $$"""
            {"Stage":"{{TransactionStages.StageFailed}}","FailedStage":"{{failedStage}}","EventId":{"Id":{{TransactionStages.StageFailedEventId}},"Name":"{{TransactionStages.StageFailed}}"},"TransactionId":"{{transactionId}}"}
            """,
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
