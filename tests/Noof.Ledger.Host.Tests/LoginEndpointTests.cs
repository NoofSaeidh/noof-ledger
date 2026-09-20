using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Noof.Ledger.Host.Tests;

public class LoginEndpointTests
{
    static WebApplicationFactory<Program> CookieMode() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Auth:Mode", "Cookie");
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
            builder.ConfigureServices(FakeUserStore.Register);
        });

    [Fact]
    public async Task A_post_without_an_antiforgery_token_is_rejected()
    {
        using var factory = CookieMode();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/account/login",
            new FormUrlEncodedContent([
                new KeyValuePair<string, string>("username", "noof"),
                new KeyValuePair<string, string>("password", "correct")]),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_login_page_renders_a_plain_post_form_with_a_token()
    {
        using var factory = CookieMode();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/account/login", TestContext.Current.CancellationToken);

        html.Should().Contain("method=\"post\"")
            .And.Contain("action=\"/account/login\"")
            .And.Contain("__RequestVerificationToken");
    }

    [Fact]
    public async Task Correct_credentials_set_an_auth_cookie()
    {
        using var factory = CookieMode();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await LoginHelper.PostWithTokenAsync(client, "noof", "correct");

        response.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue();
        cookies!.Should().Contain(c => c.Contains(".AspNetCore.Cookie", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_wrong_password_sets_no_auth_cookie()
    {
        using var factory = CookieMode();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await LoginHelper.PostWithTokenAsync(client, "noof", "wrong");

        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values
            : Enumerable.Empty<string>();

        cookies.Should().NotContain(c => c.Contains(".AspNetCore.Cookie", StringComparison.OrdinalIgnoreCase));
    }
}
