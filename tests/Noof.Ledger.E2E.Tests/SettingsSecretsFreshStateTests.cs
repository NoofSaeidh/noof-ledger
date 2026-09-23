using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;

namespace Noof.Ledger.E2E.Tests;

// Split out of SettingsSecretsTests: the "fresh install shows Not set" assertion needs a database
// that has never had AnthropicApiKey written to it. Other tests in SettingsSecretsTests
// also save that key, and IClassFixture<CookieModeHostFixture> shares one database across every test
// in a class, so this precondition can only hold reliably in a class of its own - which gets its own
// CookieModeHostFixture instance and therefore its own clone database, regardless of test order.
public sealed class SettingsSecretsFreshStateTests(CookieModeHostFixture fixture) : PageTest, IClassFixture<CookieModeHostFixture>
{
    // The model provider's secret as the page renders it into element ids. The constant left
    // Application for the provider's own folder (operator decision D-A, 2026-09-23); the value
    // cannot change - the operator's real key is stored under it - so every selector is as before.
    const string AnthropicApiKey = "anthropic-api-key";

    [Fact]
    public async Task A_fresh_install_shows_Not_set_for_every_secret()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForURLAsync("**/account/login*");
        await Page.FillAsync("input[name='username']", CookieModeHostFixture.Username);
        await Page.FillAsync("input[name='password']", CookieModeHostFixture.Password);
        await Page.ClickAsync("button[type='submit']");
        await Page.WaitForURLAsync(fixture.BaseUrl + "/");

        await Page.GotoAsync(fixture.BaseUrl + "/settings/secrets");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var status = Page.Locator($"#status-{AnthropicApiKey}");
        await Expect(status).ToContainTextAsync("Not set");
    }
}
