using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright.Xunit.v3;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence;

namespace Noof.Ledger.E2E.Tests;

// The integrity page and the bug report pages over a published host and its own clone. The host takes no fake
// explainer, so Explain is driven here only to its no-key failure; Create is FindingExplanationFlowTests' (spec P-18).
public sealed class FindingsAndReportsTests(CookieModeHostFixture fixture) : PageTest, IClassFixture<CookieModeHostFixture>
{
    const string NoDatabase = "No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.";

    static int nextMessageId = 700_000;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_failed_exchange_left_unanswered_for_two_days_is_waiting_on_you_and_links_to_its_trace()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip(NoDatabase);

        var transactionId = await SeedUnansweredExchangeAsync();

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/diagnostics/integrity");
        var index = await FindingIndexOfAsync(transactionId);

        var finding = Page.Locator($"#integrity-waiting #finding-{index}");
        await Expect(finding).ToContainTextAsync("Not applied");
        await Expect(finding).ToContainTextAsync("Waiting for: A reply to the echo");
        await Expect(finding).ToContainTextAsync("Reason: MissingReceivedAmount");
        await Expect(finding.Locator(".noof-live-value")).ToContainTextAsync("2 d");
        await Expect(Page.Locator("#integrity-bugs")).ToContainTextAsync("None");
    }

    [Fact]
    public async Task Explain_without_a_model_key_says_it_could_not_explain_and_offers_no_report()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip(NoDatabase);

        var transactionId = await SeedUnansweredExchangeAsync();

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/diagnostics/integrity");
        var index = await FindingIndexOfAsync(transactionId);

        await Page.ClickAsync($"#explain-{index}");

        await Expect(Page.Locator($"#explanation-{index}")).ToContainTextAsync("Couldn't explain this right now");
        await Expect(Page.Locator($"#create-{index}")).ToHaveCountAsync(0);
        await Expect(Page.Locator($"#explain-{index}")).ToBeEnabledAsync();
    }

    // Created two days before the real now: the host measures a record's idle time on its own clock.
    async Task<Guid> SeedUnansweredExchangeAsync()
    {
        var id = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow.AddDays(-2);

        await using var db = OpenDb();
        db.Transactions.Add(new Transaction
        {
            Id = id,
            WalletId = null,
            Kind = TransactionKind.Expense,
            RawText = "exchanged 100 eur",
            CaptureKind = CaptureKind.Text,
            Status = TransactionStatus.Failed,
            FailureReason = RecordFailureReason.MissingReceivedAmount,
            TimeZoneId = "Europe/Belgrade",
            OccurredAt = createdAt,
            OccurredOn = ZonedClock.LocalDate(createdAt, "Europe/Belgrade"),
            TelegramChatId = 1,
            TelegramMessageId = Interlocked.Increment(ref nextMessageId),
            CreatedAt = createdAt,
        });
        await db.SaveChangesAsync(Ct);

        return id;
    }

    // Other tests in this class seed findings into the same clone, so a finding is found by its trace link, whose id
    // carries the finding's index.
    async Task<int> FindingIndexOfAsync(Guid transactionId)
    {
        var link = Page.Locator($"a[id^='finding-'][href='/transactions/{transactionId}/trace']");
        await Expect(link).ToBeVisibleAsync();
        var id = await link.GetAttributeAsync("id") ?? throw new InvalidOperationException("The finding's link has no id.");

        return int.Parse(id["finding-".Length..^"-link".Length], CultureInfo.InvariantCulture);
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
