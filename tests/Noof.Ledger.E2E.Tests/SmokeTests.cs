using AwesomeAssertions;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;

namespace Noof.Ledger.E2E.Tests;

public sealed class SmokeTests(OffModeHostFixture fixture) : PageTest, IClassFixture<OffModeHostFixture>
{
    [Fact]
    public async Task Home_page_loads_with_the_ledger_title_and_no_redirect_loop()
    {
        var response = await Page.GotoAsync(fixture.BaseUrl + "/");

        response.Should().NotBeNull();
        response!.Status.Should().Be(200);
        Page.Url.Should().Be(fixture.BaseUrl + "/");
        (await Page.TitleAsync()).Should().Be("Ledger");
    }

    [Fact]
    public async Task Clicking_the_counter_button_updates_the_count_via_the_server_circuit()
    {
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
            await Page.ClickAsync("button.btn");
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

    [Fact]
    public async Task No_response_is_an_error_once_the_page_has_finished_loading()
    {
        List<string> failed = [];
        Page.Response += (_, response) =>
        {
            if (response.Status >= 400)
                failed.Add($"{response.Status} {response.Url}");
        };

        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        failed.Should().BeEmpty("every static asset and API call the served output makes must resolve");
    }
}
