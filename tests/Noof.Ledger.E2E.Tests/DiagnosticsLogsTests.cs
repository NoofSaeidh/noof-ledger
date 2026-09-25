using System.Text.RegularExpressions;
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
    public async Task Logged_at_is_shown_in_local_time_as_an_iso_like_timestamp()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var source = $"diagnostics-time-{Guid.NewGuid():N}";
        var loggedAt = new DateTimeOffset(2026, 9, 25, 21, 5, 45, TimeSpan.Zero);

        await using (var db = OpenDb())
        {
            db.AppLogs.Add(NewRow("timestamp row", loggedAt: loggedAt, source: source));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + $"/diagnostics/logs?source={source}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var expected = loggedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        await Expect(Page.Locator("#logs-grid")).ToContainTextAsync(expected);
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
    public async Task Selecting_a_quick_range_and_a_minimum_level_narrows_the_grid_to_matching_rows()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var source = $"diagnostics-quickrange-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        await using (var db = OpenDb())
        {
            db.AppLogs.Add(NewRow("old-row-outside-the-hour", loggedAt: now.AddHours(-3), source: source, level: LogSeverity.Warning));
            db.AppLogs.Add(NewRow("recent-row-below-the-level", loggedAt: now, source: source, level: LogSeverity.Information));
            db.AppLogs.Add(NewRow("recent-row-at-the-level", loggedAt: now, source: source, level: LogSeverity.Warning));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + $"/diagnostics/logs?source={source}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var grid = Page.Locator("#logs-grid");
        await Expect(grid).ToBeVisibleAsync();

        // Unfiltered (besides the shared source), all three rows are visible.
        await Expect(grid).ToContainTextAsync("old-row-outside-the-hour");
        await Expect(grid).ToContainTextAsync("recent-row-below-the-level");
        await Expect(grid).ToContainTextAsync("recent-row-at-the-level");

        // One select at a time, each confirmed before the next: QuickGrid tears down and remounts on
        // every filter change (see ReloadAsync's comment), so firing two selects back to back without
        // letting the first round-trip land races the second change against a grid still being torn
        // down and rebuilt from the first.
        await Page.SelectOptionAsync("#logs-quick-range", "LastHour");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(grid).Not.ToContainTextAsync("old-row-outside-the-hour");

        await Page.SelectOptionAsync("#logs-filter-level", "Warning");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // "Last hour" excludes the 3-hour-old row; "Warning" excludes the Information row - only
        // the row that is both recent and at Warning survives.
        await Expect(grid).ToContainTextAsync("recent-row-at-the-level");
        await Expect(grid).Not.ToContainTextAsync("recent-row-below-the-level");
        await Expect(grid).Not.ToContainTextAsync("old-row-outside-the-hour");
    }

    [Fact]
    public async Task The_sort_toggle_reverses_the_order_of_the_grid()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var source = $"diagnostics-sort-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        await using (var db = OpenDb())
        {
            db.AppLogs.Add(NewRow("sort-order-older-row", loggedAt: now.AddMinutes(-10), source: source));
            db.AppLogs.Add(NewRow("sort-order-newer-row", loggedAt: now, source: source));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + $"/diagnostics/logs?source={source}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var grid = Page.Locator("#logs-grid");
        await Expect(grid).ToBeVisibleAsync();

        // QuickGrid pads its <tbody> with empty rows up to the page size, so a row *count* assertion
        // is unreliable here; matching a regex against the grid's whole text instead proves ordering
        // directly - the pattern only matches when the first marker's text precedes the second's.
        await Expect(grid).ToContainTextAsync(new Regex("sort-order-newer-row[\\s\\S]*sort-order-older-row"));

        await Page.SelectOptionAsync("#logs-sort-order", "OldestFirst");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Expect(grid).ToContainTextAsync(new Regex("sort-order-older-row[\\s\\S]*sort-order-newer-row"));
    }

    static AppLogEntry NewRow(
        string message, DateTimeOffset? loggedAt = null, string source = "DiagnosticsLogsTests", LogSeverity level = LogSeverity.Information) => new()
    {
        Id = 0,
        LoggedAt = loggedAt ?? DateTimeOffset.UtcNow,
        Level = level,
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
