using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

public class WalletsPageSourceTests
{
    static string SourceText() => File.ReadAllText(Path.Combine(
        RepoRoot.Find().FullName, "src", "Noof.Ledger.Web", "Components", "Pages", "Wallets.razor"));

    [Fact]
    public void Is_a_routable_authorised_page()
    {
        var source = SourceText();

        source.Should().Contain("@page \"/wallets\"");
        source.Should().Contain("[Authorize]");
    }

    [Fact]
    public void Never_uses_a_popover_backed_component()
    {
        // MainLayout deliberately has no MudPopoverProvider/MudDialogProvider/MudSnackbarProvider
        // (CLAUDE.md §4, verified in ShellSourceTests) - MudSelect, MudDatePicker, MudAutocomplete,
        // MudMenu, MudTooltip, MudDialog and MudSnackbar all render through one of those providers
        // and would silently never open from a page under this layout.
        var source = SourceText();

        source.Should().NotContain("MudSelect");
        source.Should().NotContain("MudDatePicker");
        source.Should().NotContain("MudAutocomplete");
        source.Should().NotContain("MudMenu");
        source.Should().NotContain("MudTooltip");
        source.Should().NotContain("MudDialog");
        source.Should().NotContain("MudSnackbar");
    }

    [Fact]
    public void Disposes_a_component_owned_cancellation_source()
    {
        var source = SourceText();

        source.Should().Contain("@implements IDisposable");
        source.Should().Contain("CancellationTokenSource");
        source.Should().NotContain("CancellationToken.None");
    }

    [Fact]
    public void Parses_numbers_and_dates_against_invariant_culture()
    {
        // MudNumericField and a MudTextField typed DateOnly? parse user input against the server's
        // own CurrentCulture unless told otherwise - on a ru-RU/sr-Latn-RS host, "1500.50" is not a
        // valid number (comma is the decimal separator there) and the field silently keeps 0.
        var source = SourceText();
        var occurrences = source.Split("Culture=\"@CultureInfo.InvariantCulture\"").Length - 1;

        occurrences.Should().BeGreaterThanOrEqualTo(2);
    }
}
