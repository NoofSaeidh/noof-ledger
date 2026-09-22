using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Noof.Ledger.Host.Tests;

public class AuthenticationTests
{
    static WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
            builder.ConfigureServices(FakeUserStore.Register);
        });

    [Fact]
    public async Task An_anonymous_visitor_is_redirected_to_the_login_page()
    {
        using var factory = Factory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("/account/login");
    }

    [Fact]
    public async Task The_login_page_itself_is_reachable_anonymously()
    {
        using var factory = Factory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync("/account/login", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // Every page declares its own authorization and an architecture test holds it to that. Nothing
    // holds a minimal-API endpoint to it: an `app.MapGet("/spending", ...)` added in a later phase
    // answers anyone who can reach the port, and the only thing that would notice is a person
    // looking at it. The fallback policy makes the default deny rather than allow, so a forgotten
    // [Authorize] costs a redirect to the login page instead of handing out the ledger.
    [Fact]
    public void An_endpoint_that_declares_no_authorization_still_denies_anonymous_callers()
    {
        using var factory = Factory();

        var fallback = factory.Services
            .GetRequiredService<IOptions<AuthorizationOptions>>().Value.FallbackPolicy;

        fallback.Should().NotBeNull(
            "without a fallback policy every endpoint that names no policy is anonymous by default");
        fallback!.Requirements.Should().Contain(requirement => requirement is DenyAnonymousAuthorizationRequirement);
    }

    // The fallback policy above applies to every endpoint that names no policy - and MapStaticAssets
    // maps endpoints too. If it caught them, the sign-in page would still render but arrive with no
    // stylesheet and no Blazor script: a page that looks broken to the one visitor who cannot sign in
    // to report it. These are the two assets App.razor pulls in before anybody is authenticated.
    [Theory]
    [InlineData("/_content/Noof.Ledger.Web/app.css")]
    [InlineData("/_framework/blazor.web.js")]
    public async Task The_assets_the_sign_in_page_needs_are_served_anonymously(string path)
    {
        using var factory = Factory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a visitor who is not signed in yet must still be able to load the sign-in page in full");

        // Asserting the status code alone is not enough, and that is not a hypothetical: an earlier
        // version of this test did exactly that and passed while every asset came back 200 with an
        // empty body, so the sign-in page rendered as unstyled serif text. A stylesheet that arrives
        // empty is indistinguishable from one that never arrived, except that nothing reports it.
        var body = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        body.Length.Should().BeGreaterThan(200,
            "an asset that answers 200 with nothing in it leaves the page silently unstyled");
    }
}
