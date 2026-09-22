using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;

namespace Noof.Ledger.E2E.Tests;

public sealed class CounterTests(CookieModeHostFixture fixture) : PageTest, IClassFixture<CookieModeHostFixture>
{
    [Fact]
    public async Task Clicking_the_counter_button_updates_the_count_via_the_server_circuit()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/counter");

        // The click-before-SignalR-connects race is real: clicking before the circuit is up is a
        // click into nothing.
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var status = Page.Locator("p[role='status']");
        await Expect(status).ToHaveTextAsync("Current count: 0");

        // NetworkIdle can resolve before the SignalR circuit has actually finished attaching to the
        // interactive component - a click that lands in that gap is simply lost. Retrying the click
        // itself, rather than adding a fixed delay, survives that gap without slowing down the common
        // case where the circuit is already live.
        await RetryUntilAsync(async () =>
        {
            await Page.ClickAsync("#counter-increment");
            await Expect(status).ToHaveTextAsync("Current count: 1", new() { Timeout = 2_000 });
        });
    }

    static async Task RetryUntilAsync(Func<Task> attempt, int maxAttempts = 5)
    {
        for (var attemptNumber = 1; attemptNumber <= maxAttempts; attemptNumber++)
        {
            try
            {
                await attempt();
                return;
            }
            catch (PlaywrightException) when (attemptNumber < maxAttempts)
            {
            }
        }
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
