using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;

namespace Noof.Ledger.E2E.Tests;

public sealed class DiagnosticsTests(CookieModeHostFixture fixture) : PageTest, IClassFixture<CookieModeHostFixture>
{
    [Fact]
    public async Task The_diagnostics_page_lists_every_health_check()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/diagnostics");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var table = Page.Locator("#diagnostics-checks");
        await Expect(table).ToBeVisibleAsync();
        await Expect(table).ToContainTextAsync("Database");
        await Expect(table).ToContainTextAsync("Migrations");
        await Expect(table).ToContainTextAsync("Telegram");
        await Expect(table).ToContainTextAsync("AI keys");
        await Expect(table).ToContainTextAsync("Backup");
        await Expect(table).ToContainTextAsync("Disk");
        await Expect(table).ToContainTextAsync("Log sink");
    }

    [Fact]
    public async Task Entering_a_transaction_id_opens_its_trace_page()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var transactionId = Guid.NewGuid();

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/diagnostics");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Page.FillAsync("#diagnostics-transaction-id", transactionId.ToString());
        await Page.ClickAsync("#diagnostics-open-trace");

        await Page.WaitForURLAsync($"**/transactions/{transactionId}/trace");
    }

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
