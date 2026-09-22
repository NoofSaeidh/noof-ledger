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
    public async Task Saving_a_blank_value_is_refused_and_the_secret_stays_missing()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/settings/secrets");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var status = Page.Locator($"#status-{SecretKeys.TelegramOwnerChatId}");
        await Expect(status).ToContainTextAsync("Not set");

        // No FillAsync call: the field starts empty, which is exactly the "clicked Save without
        // typing anything" scenario the review proved stores state Present with "".
        await Page.Locator($"#save-{SecretKeys.TelegramOwnerChatId}").ClickAsync();

        // Storing an empty value as Present would reject every chat AND leave the owner-claim
        // recovery path disarmed, silently.
        await Expect(status).ToContainTextAsync("Not set");
        await Expect(Page.Locator($"#error-{SecretKeys.TelegramOwnerChatId}")).ToBeVisibleAsync();

        var stored = await fixture.ReadStoredSecretAsync(SecretKeys.TelegramOwnerChatId, TestContext.Current.CancellationToken);
        stored.Should().BeNull("a blank save must leave no row at all, not a Present row holding an empty string");
    }

    [Fact]
    public async Task Saving_trims_surrounding_whitespace_before_storing()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/settings/secrets");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var padded = $"   123456:e2e-trim-{Guid.NewGuid():N}   ";
        var status = Page.Locator($"#status-{SecretKeys.TelegramBotToken}");

        await RetryUntilAsync(async () =>
        {
            await Page.Locator($"#secret-{SecretKeys.TelegramBotToken}").FillAsync(padded);
            await Page.Locator($"#save-{SecretKeys.TelegramBotToken}").ClickAsync();
            await Expect(status).ToContainTextAsync("Set", new() { Timeout = 2_000 });
        });

        var stored = await fixture.ReadStoredSecretAsync(SecretKeys.TelegramBotToken, TestContext.Current.CancellationToken);
        stored.Should().Be(padded.Trim(),
            "an untrimmed leading space or trailing newline makes every call using this token 404 with only an opaque log line to show for it");
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

    [Fact]
    public async Task Testing_an_invalid_Anthropic_key_reports_failure_without_echoing_it()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");
        if (!await AnthropicIsReachableAsync())
            Assert.Skip("api.anthropic.com is not reachable from this machine.");

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/settings/secrets");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var fakeKey = $"sk-ant-invalid-{Guid.NewGuid():N}";
        var status = Page.Locator($"#status-{SecretKeys.AnthropicApiKey}");

        await RetryUntilAsync(async () =>
        {
            await Page.Locator($"#secret-{SecretKeys.AnthropicApiKey}").FillAsync(fakeKey);
            await Page.Locator($"#save-{SecretKeys.AnthropicApiKey}").ClickAsync();
            await Expect(status).ToContainTextAsync("Set", new() { Timeout = 2_000 });
        });

        var result = Page.Locator($"#test-result-{SecretKeys.AnthropicApiKey}");
        await Page.Locator($"#test-{SecretKeys.AnthropicApiKey}").ClickAsync();
        await Expect(result).ToBeVisibleAsync(new() { Timeout = 15_000 });

        var message = await result.TextContentAsync() ?? string.Empty;
        message.Should().NotBeEmpty();
        message.Should().NotContain(fakeKey, "the probe's failure message must never echo the key it was testing");
    }

    [Fact]
    public async Task A_secret_with_no_registered_probe_shows_no_Test_button()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/settings/secrets");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Phase 1B ships a probe for the Anthropic key only - the Telegram equivalent is backlog
        // (see "What this plan deliberately does NOT do"). A row with no matching ISecretProbe must
        // stay silent about it rather than showing a button that would always fail.
        await Expect(Page.Locator($"#test-{SecretKeys.TelegramBotToken}")).Not.ToBeVisibleAsync();
    }

    static async Task<bool> AnthropicIsReachableAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync("https://api.anthropic.com/v1/models");
            return true;
        }
        catch
        {
            return false;
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
