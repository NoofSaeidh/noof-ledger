using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

// AuthorizeRouteView renders NOTHING when authorization fails and no NotAuthorized fragment is
// given. That is not a hypothetical: /settings/secrets served a completely blank page to the
// operator, and it took a screenshot to notice, because the page disables prerendering and so has
// no static markup to fall back on. A blank page is the worst possible failure report - it looks
// identical to a crashed circuit, a missing script, and a bug in the page itself.
public class RouterFallbackTests
{
    static string RoutesSource() => File.ReadAllText(Path.Combine(
        RepoRoot.Find().FullName, "src", "Noof.Ledger.Web", "Components", "Routes.razor"));

    [Fact]
    public void An_unauthorized_route_renders_an_explanation_rather_than_nothing()
    {
        RoutesSource().Should().Contain("<NotAuthorized>",
            "AuthorizeRouteView with no NotAuthorized content renders an empty page, which is "
            + "indistinguishable from a crash; whatever goes wrong, the operator must be told something");
    }

    [Fact]
    public void The_unauthorized_explanation_offers_a_way_back_in()
    {
        RoutesSource().Should().Contain("/account/login",
            "telling someone they are not authorized without a link to sign in leaves them stuck on "
            + "a page with no exit");
    }
}
