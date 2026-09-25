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

    static AppLogEntry NewRow(string message) => new()
    {
        Id = 0,
        LoggedAt = DateTimeOffset.UtcNow,
        Level = LogSeverity.Information,
        Source = "DiagnosticsLogsTests",
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
