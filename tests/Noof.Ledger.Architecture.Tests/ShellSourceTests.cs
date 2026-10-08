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

    static string WalletsSource() => Read("Components", "Pages", "Wallets.razor");

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

    // Download open reports calls noofLedger.downloadText; with the tag missing the button does nothing, nothing logs
    // and nothing throws - the same silent class of defect as a missing stylesheet.
    [Fact]
    public void The_document_loads_the_download_script_after_the_component_library()
    {
        var app = AppSource();
        const string Download = "_content/Noof.Ledger.Web/download.js";

        app.Should().Contain(Download, "the bug reports page downloads its Markdown through this script");
        app.IndexOf(Download, StringComparison.Ordinal).Should().BeGreaterThan(
            app.IndexOf("_content/MudBlazor/MudBlazor.min.js", StringComparison.Ordinal));
        Read("wwwroot", "download.js").Should().Contain("window.noofLedger.downloadText");
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

    // Master's 8fe9d25 fixed exactly this class of bug for the rest of app.css: a rule painted for
    // one color scheme rendered wrong inside the other, because it named a literal color instead of
    // one of MudThemeProvider's own --mud-palette-* variables, which already flip with the theme.
    // .trace-row-warning (Phase 6) named #fff3cd/#7a4a00 outright.
    [Fact]
    public void The_trace_timelines_warning_row_uses_a_theme_variable_not_a_literal_color()
    {
        var css = Read("wwwroot", "app.css");

        css.Should().NotContain("#fff3cd");
        css.Should().NotContain("#7a4a00");
        css.Should().Contain(".trace-row-warning");
        css.Should().MatchRegex(
            @"\.trace-row-warning\s*\{[^}]*var\(--mud-palette-warning[^}]*\}",
            "a warning row must take its color from the theme, the same way .noof-level-warning does");
    }

    // Every other filter/field caption on Transactions, DiagnosticsLogs and Wallets itself
    // (Name/Aliases) is a real <label for>, not just styled text - a bare <MudText> caption reads
    // fine but is not reachable from the control it describes. Wallets' payment-default caption
    // (Phase 6) was the one exception.
    [Fact]
    public void The_wallet_payment_default_caption_is_a_real_label_for_the_select()
    {
        WalletsSource().Should().Contain(
            "HtmlTag=\"label\" for=\"@($\"wallet-payment-default-{wallet.Id}\")\"",
            "every other caption beside a filter or form control in this app is a <label for>, not bare text");
    }
}
