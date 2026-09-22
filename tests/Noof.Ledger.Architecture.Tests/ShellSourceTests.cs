using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

// A missing stylesheet link is the same class of defect as the missing <NotAuthorized> fragment that
// served a blank /settings/secrets: the app still answers 200, every browser test still passes, and
// the only detector is a person looking at it. These three assertions are that detector.
public class ShellSourceTests
{
    static string Read(params string[] pathSegments) => File.ReadAllText(Path.Combine(
        [RepoRoot.Find().FullName, "src", "Noof.Ledger.Web", .. pathSegments]));

    static string AppSource() => Read("Components", "App.razor");

    static string LayoutSource() => Read("Components", "Layout", "MainLayout.razor");

    [Fact]
    public void The_document_loads_the_component_library_stylesheet()
    {
        AppSource().Should().Contain("_content/MudBlazor/MudBlazor.min.css",
            "without it every page renders as unstyled serif text while still answering 200");
    }

    [Fact]
    public void The_document_loads_the_component_library_script()
    {
        AppSource().Should().Contain("_content/MudBlazor/MudBlazor.min.js",
            "components that need JavaScript fail silently without it - nothing logs, nothing throws");
    }

    [Fact]
    public void The_layout_hosts_a_theme_provider()
    {
        LayoutSource().Should().Contain("<MudThemeProvider",
            "the theme is where the palette lives; with no provider MudBlazor falls back to its own "
            + "default light palette and the app silently stops looking like itself");
    }

    [Fact]
    public void The_shell_does_not_fetch_a_typeface_from_the_internet()
    {
        AppSource().Should().NotContain("fonts.googleapis.com",
            "this app binds to loopback and is meant to work with the network down; a stylesheet "
            + "fetched from Google on every page load contradicts both. NoofTheme sets a system font "
            + "stack instead");
    }

    [Fact]
    public void The_layout_hosts_no_provider_that_cannot_work_where_it_is()
    {
        // MudBlazor documents that its providers "must render in the same interactive render mode as
        // the components that use them", and this layout renders statically under per-page
        // interactivity. A popover, dialog or snackbar provider placed here would be inert - and an
        // inert provider is worse than an absent one, because the next person to reach for a dialog
        // finds it silently doing nothing rather than finding out why.
        var layout = LayoutSource();

        layout.Should().NotContain("<MudPopoverProvider");
        layout.Should().NotContain("<MudDialogProvider");
        layout.Should().NotContain("<MudSnackbarProvider");
    }
}
