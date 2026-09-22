using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

// README.md claims "[Authorize] ships on every page from the first commit, so a forgotten flag
// cannot leave a route ungated." That was a convention, not an enforcement, and it had already
// drifted: Counter.razor - the Blazor template's scaffold page, kept because a smoke test uses it
// to prove the interactive circuit works - was reachable anonymously. A claim about authorization
// in a public README should be a test, not a habit.
public class RoutablePageAuthorizationTests
{
    static IEnumerable<FileInfo> RoutablePages() =>
        new DirectoryInfo(Path.Combine(RepoRoot.Find().FullName, "src", "Noof.Ledger.Web"))
            .EnumerateFiles("*.razor", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file.FullName).Contains("@page ", StringComparison.Ordinal));

    [Fact]
    public void Every_routable_page_declares_its_authorization()
    {
        var undeclared = RoutablePages()
            .Where(file =>
            {
                var source = File.ReadAllText(file.FullName);
                return !source.Contains("[Authorize]", StringComparison.Ordinal)
                    && !source.Contains("[AllowAnonymous]", StringComparison.Ordinal);
            })
            .Select(file => file.Name)
            .ToList();

        undeclared.Should().BeEmpty(
            "a page that says nothing about authorization is reachable by anyone who can reach the port");
    }

    [Fact]
    public void Only_the_login_page_is_anonymous()
    {
        var anonymous = RoutablePages()
            .Where(file => File.ReadAllText(file.FullName).Contains("[AllowAnonymous]", StringComparison.Ordinal))
            .Select(file => file.Name)
            .ToList();

        anonymous.Should().BeEquivalentTo(["Login.razor"],
            "the sign-in page is the one route that must answer before anyone is signed in; every "
            + "other anonymous page is a hole, and this list is the place to argue about a new one");
    }

    [Fact]
    public void There_is_at_least_one_routable_page_to_check()
    {
        RoutablePages().Should().NotBeEmpty(
            "a rule whose subject set is empty passes forever and enforces nothing");
    }
}
