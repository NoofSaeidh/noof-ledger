using AwesomeAssertions;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;

namespace Noof.Ledger.E2E.Tests;

public sealed class SmokeTests(UnreachableDatabaseHostFixture fixture) : PageTest, IClassFixture<UnreachableDatabaseHostFixture>
{
    // Phase 0b's guarantee was "the front page answers honestly when PostgreSQL is down, not a
    // 500". Cookie authentication is mandatory now, so an anonymous GET / never reaches the Home
    // page at all - it is challenged and redirected before any database access is attempted. What
    // survives, and is still worth proving: the redirect itself, and the page it lands on, never
    // surface the database outage as a server error.
    [Fact]
    public async Task Anonymous_home_page_redirects_to_login_without_a_5xx_while_the_database_is_down()
    {
        var response = await Page.GotoAsync(fixture.BaseUrl + "/");

        response.Should().NotBeNull();
        response!.Status.Should().BeLessThan(500, "an unreachable database must never surface as a server error");
        Page.Url.Should().StartWith(fixture.BaseUrl + "/account/login");
        (await Page.TitleAsync()).Should().Be("Sign in");
    }

    [Fact]
    public async Task Healthz_answers_200_while_the_database_is_down()
    {
        var response = await Page.GotoAsync(fixture.BaseUrl + "/healthz");

        response.Should().NotBeNull();
        response!.Status.Should().Be(200);
    }

    [Fact]
    public async Task No_response_is_an_error_once_the_login_page_has_finished_loading()
    {
        List<string> failed = [];
        Page.Response += (_, response) =>
        {
            if (response.Status >= 400)
                failed.Add($"{response.Status} {response.Url}");
        };

        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        failed.Should().BeEmpty("every static asset and API call the redirected login page makes must resolve");
    }
}
