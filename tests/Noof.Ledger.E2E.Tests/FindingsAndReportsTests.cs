using System.Globalization;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence;
using Npgsql;
using NpgsqlTypes;

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

    [Fact]
    public async Task The_bug_reports_page_lists_open_reports_and_All_adds_the_closed_ones()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip(NoDatabase);

        var marker = Guid.NewGuid().ToString("N");
        var longText = $"open report {marker} " + new string('x', 100);
        var open = await SeedDashboardReportAsync(longText);
        var closed = await SeedDashboardReportAsync($"closed report {marker}", closed: true);

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/bugs");

        var openRow = Page.Locator($"#bug-{open}");
        await Expect(openRow).ToContainTextAsync($"#{open}");
        await Expect(openRow).ToContainTextAsync("2026-09-30 10:00 UTC");
        await Expect(openRow.Locator("td").Nth(2)).ToHaveTextAsync(longText[..80] + "…");
        await Expect(openRow).ToContainTextAsync("Open");
        await Expect(Page.Locator($"#bug-{closed}")).ToHaveCountAsync(0);
        await Expect(Page.Locator("#bugs-filter-open")).ToHaveAttributeAsync("aria-current", "page");

        await Page.ClickAsync("#bugs-filter-all");

        await Expect(Page.Locator($"#bug-{closed}")).ToContainTextAsync("Closed");
        await Expect(Page.Locator("#bugs-filter-all")).ToHaveAttributeAsync("aria-current", "page");
    }

    [Fact]
    public async Task Download_open_reports_saves_one_markdown_file_holding_each_open_report()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip(NoDatabase);

        var text = $"download me {Guid.NewGuid():N}";
        var number = await SeedDashboardReportAsync(text);

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/bugs");
        await Expect(Page.Locator($"#bug-{number}")).ToBeVisibleAsync();

        var download = await Page.RunAndWaitForDownloadAsync(() => Page.ClickAsync("#bugs-download"));

        download.SuggestedFilename.Should().MatchRegex(@"^bug-reports-\d{4}-\d{2}-\d{2}-\d{4}\.md$");
        var markdown = await File.ReadAllTextAsync(await download.PathAsync(), Ct);
        markdown.Should().StartWith("# noof-ledger bug reports\n");
        markdown.Should().Contain($"## Bug report #{number}");
        markdown.Should().Contain(text);
    }

    [Fact]
    public async Task The_nav_bar_links_to_bug_reports_and_marks_it_current_there()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip(NoDatabase);

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/bugs");

        var link = Page.Locator(".noof-appbar a", new PageLocatorOptions { HasText = "Bugs" });
        await Expect(link).ToHaveAttributeAsync("href", "/bugs");
        await Expect(link).ToHaveAttributeAsync("aria-current", "page");
    }

    [Fact]
    public async Task Close_and_Reopen_change_the_reports_status_on_its_page()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip(NoDatabase);

        var number = await SeedDashboardReportAsync($"close me {Guid.NewGuid():N}");

        await SignInAsync();
        await Page.GotoAsync($"{fixture.BaseUrl}/bugs/{number}");

        var meta = Page.Locator("#bug-report-meta");
        await Expect(meta).ToHaveTextAsync("Filed 2026-09-30 10:00 UTC · Dashboard · Open");
        await Expect(Page.Locator("#bug-report-explanation")).ToContainTextAsync("Seeded explanation.");
        await Expect(Page.Locator("#bug-report-explanation")).ToContainTextAsync("Looks like a bug in the app.");
        await Expect(Page.Locator("#bug-report-findings-then")).ToContainTextAsync("Not collected");
        await Expect(Page.Locator("#bug-report-findings-now")).ToHaveCountAsync(0);

        await Page.ClickAsync("#bug-report-close");
        await Expect(meta).ToHaveTextAsync("Filed 2026-09-30 10:00 UTC · Dashboard · Closed");

        await Page.ClickAsync("#bug-report-reopen");
        await Expect(meta).ToHaveTextAsync("Filed 2026-09-30 10:00 UTC · Dashboard · Open");
        await Expect(Page.Locator("#bug-report-close")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Copy_as_Markdown_puts_the_report_on_the_clipboard_beside_the_privacy_warning()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip(NoDatabase);

        var number = await SeedDashboardReportAsync($"copy me {Guid.NewGuid():N}");
        // Reading the real clipboard back in headless Chromium needs the page focused, which Playwright does not
        // guarantee: MudBlazor's copyToClipboard calls navigator.clipboard.writeText, so the test records what is
        // written there instead.
        await Page.AddInitScriptAsync("""
            Object.defineProperty(navigator, "clipboard", {
                configurable: true,
                value: { writeText: text => { window.__copied = text; return Promise.resolve(); } },
            });
            """);

        await SignInAsync();
        await Page.GotoAsync($"{fixture.BaseUrl}/bugs/{number}");

        await Expect(Page.Locator("#bug-report-copy-warning"))
            .ToHaveTextAsync("Contains your data — never paste it into a public issue.");
        await Page.ClickAsync("#bug-report-copy");

        await Expect(Page.Locator("#bug-report-copied")).ToContainTextAsync("Copied.");
        await Page.WaitForFunctionAsync(
            "expected => typeof window.__copied === 'string' && window.__copied.includes(expected)", $"## Bug report #{number}");
    }

    [Fact]
    public async Task An_unknown_report_number_says_there_is_none()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip(NoDatabase);

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/bugs/999999");

        await Expect(Page.Locator("#bug-report-missing")).ToContainTextAsync("No bug report #999999.");
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

    // A Dashboard report, so no Telegram id is needed; Done, so the check constraints hold. Its time is a fixed literal.
    async Task<int> SeedDashboardReportAsync(string text, bool closed = false)
    {
        var filedAt = new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO bug_reports (id, created_at, source, text, status, closed_at, explanation_state,
                explanation_attempts, explanation_next_at, explanation, looks_like_bug)
            VALUES (@id, @filedAt, 1, @text, @status, @closedAt, 1, 0, @filedAt, 'Seeded explanation.', true)
            RETURNING number
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = Guid.NewGuid() });
        command.Parameters.Add(new NpgsqlParameter("filedAt", NpgsqlDbType.TimestampTz) { Value = filedAt });
        command.Parameters.Add(new NpgsqlParameter("text", NpgsqlDbType.Text) { Value = text });
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Integer) { Value = closed ? 1 : 0 });
        command.Parameters.Add(new NpgsqlParameter("closedAt", NpgsqlDbType.TimestampTz)
        {
            Value = closed ? filedAt.AddHours(1) : DBNull.Value,
        });

        return Convert.ToInt32(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
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
