using AwesomeAssertions;
using Microsoft.Playwright.Xunit.v3;

namespace Noof.Ledger.E2E.Tests;

public sealed class LoginTests(CookieModeHostFixture fixture) : PageTest, IClassFixture<CookieModeHostFixture>
{
    [Fact]
    public async Task Signing_in_sets_a_real_cookie_and_reaches_the_authorized_home_page()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        // Anonymous redirects to /account/login - that is the start of the round trip, not a
        // failure.
        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForURLAsync("**/account/login*");

        await Page.FillAsync("input[name='username']", CookieModeHostFixture.Username);
        await Page.FillAsync("input[name='password']", CookieModeHostFixture.Password);

        // A real form POST, exercising the antiforgery token the page rendered, rather than a
        // hand-rolled request - that is the entire point of driving this through the browser.
        await Page.ClickAsync("button[type='submit']");
        await Page.WaitForURLAsync(fixture.BaseUrl + "/");

        var cookies = await Context.CookiesAsync();
        cookies.Should().Contain(c => c.Name.Contains(".AspNetCore.Cookie", StringComparison.OrdinalIgnoreCase));

        await Expect(Page.Locator("h1")).ToHaveTextAsync("Ledger");
    }
}
