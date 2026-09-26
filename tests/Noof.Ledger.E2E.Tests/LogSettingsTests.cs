using AwesomeAssertions;
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

    // Minor finding, Phase 5 final review: the retention block reused .noof-filter-bar's
    // auto-fill(12rem) grid, which fit only five of the six inputs per row at typical desktop
    // widths and orphaned Fatal alone on a second row - a fixed 3-column grid lays them out 3x2
    // instead, so no row ever holds fewer than 3 of the six.
    [Fact]
    public async Task At_desktop_width_no_retention_input_is_orphaned_alone_on_its_own_row()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await Page.SetViewportSizeAsync(1440, 900);
        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/diagnostics/logs/settings");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var tops = await Page.Locator(
            "#retention-verbose, #retention-debug, #retention-information, #retention-warning, #retention-error, #retention-fatal")
            .EvaluateAllAsync<double[]>("els => els.map(e => e.getBoundingClientRect().top)");

        var rowSizes = tops.GroupBy(top => top).Select(row => row.Count());

        rowSizes.Should().AllSatisfy(count => count.Should().BeGreaterThan(1),
            "the fixed 3-column grid must lay six inputs out 3x2, never leaving one alone on its own row");
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
