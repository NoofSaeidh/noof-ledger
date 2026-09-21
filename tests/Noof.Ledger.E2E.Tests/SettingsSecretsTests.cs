using AwesomeAssertions;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;
using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.E2E.Tests;

public sealed class SettingsSecretsTests(CookieModeHostFixture fixture) : PageTest, IClassFixture<CookieModeHostFixture>
{
    [Fact]
    public async Task Anonymous_visitor_is_redirected_and_a_signed_in_visitor_saves_through_the_circuit()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await Page.GotoAsync(fixture.BaseUrl + "/settings/secrets");
        await Page.WaitForURLAsync("**/account/login*");

        await Page.FillAsync("input[name='username']", CookieModeHostFixture.Username);
        await Page.FillAsync("input[name='password']", CookieModeHostFixture.Password);
        await Page.ClickAsync("button[type='submit']");
        await Page.WaitForURLAsync(fixture.BaseUrl + "/");

        await Page.GotoAsync(fixture.BaseUrl + "/settings/secrets");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var status = Page.Locator($"#status-{SecretKeys.AnthropicApiKey}");
        await Expect(status).ToContainTextAsync("Not set");

        var value = $"sk-e2e-{Guid.NewGuid():N}";
        await RetryUntilAsync(async () =>
        {
            await Page.Locator($"#secret-{SecretKeys.AnthropicApiKey}").FillAsync(value);
            await Page.Locator($"#save-{SecretKeys.AnthropicApiKey}").ClickAsync();
            await Expect(status).ToContainTextAsync("Set", new() { Timeout = 2_000 });
        });
    }

    [Fact]
    public async Task No_captured_log_line_contains_a_value_submitted_through_this_page()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/settings/secrets");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var status = Page.Locator($"#status-{SecretKeys.TelegramBotToken}");
        var value = $"sk-e2e-log-check-{Guid.NewGuid():N}";

        await RetryUntilAsync(async () =>
        {
            await Page.Locator($"#secret-{SecretKeys.TelegramBotToken}").FillAsync(value);
            await Page.Locator($"#save-{SecretKeys.TelegramBotToken}").ClickAsync();
            await Expect(status).ToContainTextAsync("Set", new() { Timeout = 2_000 });
        });

        fixture.CapturedOutputLines.Should().NotContain(
            line => line.Contains(value, StringComparison.Ordinal),
            "a secret value must never reach the console log");
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
}
