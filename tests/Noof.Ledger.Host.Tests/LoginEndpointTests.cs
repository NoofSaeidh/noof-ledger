using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Domain;
using Noof.Ledger.Host.Auth;

namespace Noof.Ledger.Host.Tests;

public class LoginEndpointTests
{
    static WebApplicationFactory<Program> CookieMode() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
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

        var response = await client.PostAsync("/account/login/submit",
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
            .And.Contain("action=\"/account/login/submit\"")
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

    [Fact]
    public async Task An_unknown_username_still_pays_the_verify_cost_exactly_once()
    {
        var counting = new CountingPasswordHasher(new PasswordHasherAdapter());

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
            builder.ConfigureServices(services =>
            {
                FakeUserStore.Register(services);
                services.AddSingleton<IPasswordHasher>(counting);
            });
        });
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        await LoginHelper.PostWithTokenAsync(client, "does-not-exist", "whatever");

        counting.VerifyCallCount.Should().Be(1);
    }

    sealed class CountingPasswordHasher(IPasswordHasher inner) : IPasswordHasher
    {
        public int VerifyCallCount { get; private set; }

        public string Hash(AppUser user, string password) => inner.Hash(user, password);

        public PasswordVerifyResult Verify(AppUser user, string hash, string password)
        {
            VerifyCallCount++;
            return inner.Verify(user, hash, password);
        }
    }
}
