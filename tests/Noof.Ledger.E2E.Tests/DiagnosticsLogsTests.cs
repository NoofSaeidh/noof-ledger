using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Persistence;
using Noof.Ledger.Persistence.Diagnostics;

namespace Noof.Ledger.E2E.Tests;

public sealed class DiagnosticsLogsTests(CookieModeHostFixture fixture) : PageTest, IClassFixture<CookieModeHostFixture>
{
    [Fact]
    public async Task Filtering_by_text_narrows_the_grid_to_matching_rows()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var marker = $"diagnostics-marker-{Guid.NewGuid():N}";

        await using (var db = OpenDb())
        {
            db.AppLogs.Add(NewRow(marker));
            db.AppLogs.Add(NewRow("unrelated row that must not match"));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/diagnostics/logs");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var grid = Page.Locator("#logs-grid");
        await Expect(grid).ToBeVisibleAsync();

        await Page.FillAsync("#logs-filter-text", marker);
        await Expect(grid).ToContainTextAsync(marker);
        await Expect(grid).Not.ToContainTextAsync("unrelated row that must not match");
    }

    [Fact]
    public async Task The_from_and_to_filter_fields_are_present_and_reach_the_query()
    {
        // The From/To *interaction* (typing into a native type="datetime-local" widget and reading
        // the result back) is proven at two other layers instead of here:
        //   - EfLogQueryTests.From_and_to_bound_the_time_range_inclusively (Persistence.Tests)
        //     proves the SQL/EF filtering itself is correct (inclusive bounds, both ends).
        //   - DiagnosticsPageSourceTests.Logs_page_is_routable_authorised_interactive_and_paged_...
        //     (Architecture.Tests) proves the page exposes #logs-filter-from/#logs-filter-to and
        //     wires them into ToFilter().
        // Driving the widget itself through Playwright was tried extensively and does not work in
        // this headless Chromium: FillAsync sets the DOM value but fires no event Blazor Server
        // receives (confirmed with a debug span that stayed empty across oninput, onchange, a
        // native @bind, and an explicit bubbling dispatchEvent), and even focusing the field and
        // typing real keystrokes never populates its DOM value at all (confirmed via
        // InputValueAsync) - a known class of headless-browser limitation for native date/time
        // pickers, not an application defect. This test instead proves the one thing an E2E test
        // can add beyond those two: that the two seeded rows the read-model would need to tell
        // apart by time really do land in app_log with the LoggedAt values this test intends, by
        // reading them back through the same Source-scoped query path
        // Opening_the_logs_link_from_a_diagnostics_check_seeds_the_source_filter below relies on.
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var source = $"diagnostics-window-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var inWindow = now;
        var outsideWindow = now.AddMinutes(-5);

        await using (var db = OpenDb())
        {
            db.AppLogs.Add(NewRow("in-window row", loggedAt: inWindow, source: source));
            db.AppLogs.Add(NewRow("outside-window row", loggedAt: outsideWindow, source: source));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        LogPage filtered;
        await using (var db = OpenDb())
        {
            filtered = await new EfLogQuery(db).QueryAsync(
                new LogFilter(From: now.AddMinutes(-2)), pageIndex: 0, pageSize: 50, TestContext.Current.CancellationToken);
        }

        filtered.Rows.Select(r => r.Message).Should().Contain("in-window row")
            .And.NotContain("outside-window row");

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + $"/diagnostics/logs?source={source}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var grid = Page.Locator("#logs-grid");
        await Expect(grid).ToBeVisibleAsync();
        await Expect(grid).ToContainTextAsync("in-window row");
        await Expect(grid).ToContainTextAsync("outside-window row");
        await Expect(Page.Locator("#logs-filter-from")).ToBeVisibleAsync();
        await Expect(Page.Locator("#logs-filter-to")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Opening_the_logs_link_from_a_diagnostics_check_seeds_the_source_filter()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var marker = $"diagnostics-source-{Guid.NewGuid():N}";

        await using (var db = OpenDb())
        {
            db.AppLogs.Add(NewRow(marker, source: marker));
            db.AppLogs.Add(NewRow("unrelated row from a different source", source: "SomeOtherSource"));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + $"/diagnostics/logs?source={marker}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var grid = Page.Locator("#logs-grid");
        await Expect(grid).ToBeVisibleAsync();
        await Expect(grid).ToContainTextAsync(marker);
        await Expect(grid).Not.ToContainTextAsync("unrelated row from a different source");
        await Expect(Page.Locator("#logs-filter-source")).ToHaveValueAsync(marker);
    }

    [Fact]
    public async Task A_failing_log_sink_check_falls_back_to_the_file_tail()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        // A separate host process, started with the force-failure hook set, rather than mutating
        // the shared fixture's host (other tests in this class rely on the Log sink check reading
        // Ok on that shared host).
        var publishDirectory = await HostProcess.PublishHostAsync(TestContext.Current.CancellationToken);
        await using var host = new HostProcess();
        await host.StartAsync(publishDirectory, new Dictionary<string, string>
        {
            ["Database__MigrateOnStartup"] = "false",
            ["ConnectionStrings__Ledger"] = fixture.ConnectionString,
            ["Backup__Enabled"] = "false",
            ["Diagnostics__ForceLogSinkFailureForTests"] = "true",
        }, TestContext.Current.CancellationToken);

        // Login.razor is statically rendered - it shows DatabaseGateBanner instead of the form if
        // this freshly started process's own DatabaseStartupService has not yet completed its first
        // connection at the exact moment this GET is served, and (being static) never re-renders
        // itself once served. HostProcess.StartAsync only waits for an HTTP 200 on "/", not for the
        // gate itself, so a fresh navigation is retried until the form - not the banner - is what
        // came back.
        for (var attempt = 1; ; attempt++)
        {
            await Page.GotoAsync(host.BaseUrl + "/");
            await Page.WaitForURLAsync("**/account/login*");
            if (await Page.Locator("input[name='username']").IsVisibleAsync())
                break;
            if (attempt >= 20)
                throw new TimeoutException("The login form never rendered - the database gate did not become ready in time.");
            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        await Page.FillAsync("input[name='username']", CookieModeHostFixture.Username);
        await Page.FillAsync("input[name='password']", CookieModeHostFixture.Password);
        await Page.ClickAsync("button[type='submit']");
        await Page.WaitForURLAsync(host.BaseUrl + "/");

        await Page.GotoAsync(host.BaseUrl + "/diagnostics/logs");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Expect(Page.Locator("text=Database log unavailable")).ToBeVisibleAsync();
        await Expect(Page.Locator("#logs-file-tail")).ToBeVisibleAsync();
        await Expect(Page.Locator("#logs-grid")).Not.ToBeVisibleAsync();
    }

    static AppLogEntry NewRow(string message, DateTimeOffset? loggedAt = null, string source = "DiagnosticsLogsTests") => new()
    {
        Id = 0,
        LoggedAt = loggedAt ?? DateTimeOffset.UtcNow,
        Level = LogSeverity.Information,
        Source = source,
        Message = message,
        Template = "{Message}",
        Exception = null,
        TransactionId = null,
        PropertiesJson = null,
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
