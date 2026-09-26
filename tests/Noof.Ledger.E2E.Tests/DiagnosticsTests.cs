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
    public async Task The_logs_link_for_a_non_ok_check_opens_its_owning_logger_category_not_the_check_name()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/diagnostics");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // The fixture's host is never given an AI provider key, so "AI keys" is reliably non-Ok
        // and carries a Logs link (I-1: the link must open Noof.Ledger.Ai, the logger category
        // that check's own work runs under, not the literal display name "AI keys").
        var row = Page.Locator("#diagnostics-checks tr", new PageLocatorOptions { HasText = "AI keys" });
        var link = row.Locator("a", new LocatorLocatorOptions { HasText = "Logs" });

        await Expect(link).ToHaveAttributeAsync("href", "/diagnostics/logs?source=Noof.Ledger.Ai");
    }

    [Fact]
    public async Task A_view_all_logs_link_opens_the_logs_page()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/diagnostics");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Page.ClickAsync("#diagnostics-view-all-logs");

        await Page.WaitForURLAsync($"**/diagnostics/logs");
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
