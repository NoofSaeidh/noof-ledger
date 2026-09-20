using System.Security.Claims;
using System.Text.Encodings.Web;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Noof.Ledger.Host.Auth;

namespace Noof.Ledger.Host.Tests;

public class LocalOwnerHandlerTests
{
    static LocalOwnerHandler CreateHandler()
    {
        var options = Options.Create(new AuthenticationSchemeOptions());
        var monitor = new StaticOptionsMonitor(options.Value);

        return new LocalOwnerHandler(monitor, NullLoggerFactory.Instance, UrlEncoder.Default);
    }

    [Fact]
    public async Task Authenticates_every_request_as_the_local_owner()
    {
        var handler = CreateHandler();
        var scheme = new AuthenticationScheme(AuthSchemes.LocalOwner, null, typeof(LocalOwnerHandler));
        await handler.InitializeAsync(scheme, new DefaultHttpContext());

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeTrue();
        result.Principal!.Identity!.IsAuthenticated.Should().BeTrue();
        result.Principal.FindFirst(ClaimTypes.Name)!.Value.Should().Be("local owner");
        result.Principal.FindFirst(ClaimTypes.NameIdentifier).Should().NotBeNull();
    }

    sealed class StaticOptionsMonitor(AuthenticationSchemeOptions value)
        : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue => value;

        public AuthenticationSchemeOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }
}
