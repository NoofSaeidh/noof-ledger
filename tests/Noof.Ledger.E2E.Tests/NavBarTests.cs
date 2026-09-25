using AwesomeAssertions;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;

namespace Noof.Ledger.E2E.Tests;

public sealed class NavBarTests(CookieModeHostFixture fixture) : PageTest, IClassFixture<CookieModeHostFixture>
{
    [Fact]
    public async Task The_nav_bar_has_a_Logs_link_next_to_Diagnostics()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/diagnostics");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var logsLink = Page.Locator(".noof-appbar a", new PageLocatorOptions { HasText = "Logs" });
        await Expect(logsLink).ToBeVisibleAsync();
        await Expect(logsLink).ToHaveAttributeAsync("href", "/diagnostics/logs");
    }

    // I-2 (this fix round): a plain StartsWith highlighted both "Diagnostics" and "Logs" on
    // /diagnostics/logs, since "/diagnostics" is a string-prefix of "/diagnostics/logs". Comparing
    // computed colour rather than a guessed MudBlazor class name keeps this test independent of
    // MudButton's own internal naming.
    [Fact]
    public async Task Visiting_the_logs_page_highlights_only_Logs_not_Diagnostics()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/diagnostics/logs");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var diagnosticsColor = await ComputedColorAsync("Diagnostics");
        var logsColor = await ComputedColorAsync("Logs");
        var walletsColor = await ComputedColorAsync("Wallets");

        // Wallets is definitely not current on /diagnostics/logs, so its colour is the
        // "not current" baseline - Diagnostics must match it, and Logs must not.
        diagnosticsColor.Should().Be(walletsColor);
        logsColor.Should().NotBe(walletsColor);
    }

    [Fact]
    public async Task Visiting_diagnostics_itself_highlights_only_Diagnostics_not_Logs()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/diagnostics");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var diagnosticsColor = await ComputedColorAsync("Diagnostics");
        var logsColor = await ComputedColorAsync("Logs");
        var walletsColor = await ComputedColorAsync("Wallets");

        logsColor.Should().Be(walletsColor);
        diagnosticsColor.Should().NotBe(walletsColor);
    }

    async Task<string> ComputedColorAsync(string label) =>
        await Page.Locator(".noof-appbar a", new PageLocatorOptions { HasText = label })
            .EvaluateAsync<string>("el => getComputedStyle(el).color");

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
