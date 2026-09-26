using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;

namespace Noof.Ledger.E2E.Tests;

public sealed class LogSettingsTests(CookieModeHostFixture fixture) : PageTest, IClassFixture<CookieModeHostFixture>
{
    [Fact]
    public async Task Changing_level_and_retention_then_Save_persists_across_a_reload()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/diagnostics/logs/settings");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Page.SelectOptionAsync("#log-settings-level", "Debug");
        await Page.FillAsync("#retention-debug", "5");
        await Page.FillAsync("#retention-warning", "45");
        await Page.Locator("#log-settings-save").ClickAsync();

        await Expect(Page.Locator("#log-settings-feedback")).ToContainTextAsync("Saved");

        await Page.ReloadAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Expect(Page.Locator("#log-settings-level")).ToHaveValueAsync("Debug");
        await Expect(Page.Locator("#retention-debug")).ToHaveValueAsync("5");
        await Expect(Page.Locator("#retention-warning")).ToHaveValueAsync("45");

        // The Logs page header reads the applied level, not the last edit - proving Save (not the
        // page load above) is what actually applied it.
        await Page.GotoAsync(fixture.BaseUrl + "/diagnostics/logs");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator("#logs-settings-link")).ToContainTextAsync("Debug");
    }

    [Fact]
    public async Task Editing_a_field_without_clicking_Save_changes_nothing()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/diagnostics/logs/settings");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var before = await Page.Locator("#log-settings-level").InputValueAsync();

        await Page.SelectOptionAsync("#log-settings-level", "Off");
        await Page.FillAsync("#retention-fatal", "999");

        await Page.ReloadAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Expect(Page.Locator("#log-settings-level")).ToHaveValueAsync(before);
        await Expect(Page.Locator("#retention-fatal")).Not.ToHaveValueAsync("999");
    }

    [Fact]
    public async Task An_out_of_range_retention_value_is_refused_with_an_inline_alert()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/diagnostics/logs/settings");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Page.FillAsync("#retention-information", "0");
        await Page.Locator("#log-settings-save").ClickAsync();

        var feedback = Page.Locator("#log-settings-feedback");
        await Expect(feedback).ToBeVisibleAsync();
        await Expect(feedback).ToContainTextAsync("Information");
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
