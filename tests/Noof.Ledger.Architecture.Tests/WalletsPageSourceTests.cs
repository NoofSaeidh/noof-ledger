using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

public class WalletsPageSourceTests
{
    static string SourceText() => File.ReadAllText(Path.Combine(
        RepoRoot.Find().FullName, "src", "Noof.Ledger.Web", "Components", "Pages", "Wallets.razor"));

    static List<string> NumericFields() =>
        [.. SourceText().Split("<MudNumericField").Skip(1).Select(rest => rest[..rest.IndexOf("/>", StringComparison.Ordinal)])];

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
    public void Shows_the_database_gate_banner_while_the_database_is_not_ready()
    {
        // Task 1 covered Login and Home only; Phase 5's spec (§1, §4) is that a signed-in user sees
        // the waiting banner on EVERY page instead of its data - Wallets is one of those pages.
        var source = SourceText();

        source.Should().Contain("IDatabaseGate");
        source.Should().Contain("DatabaseGateBanner");
    }

    [Fact]
    public void Does_not_offer_a_payment_default_control_on_an_archived_row()
    {
        // An archived wallet's payment default is cleared on archive and refused if set again
        // (EfWalletAdmin.SetPaymentDefaultAsync) - the control it would silently no-op through
        // should not be offered, the same way the currency-default button is not offered.
        var source = SourceText();
        var paymentDefaultRow = source.IndexOf("wallet-payment-default-", StringComparison.Ordinal);
        var guard = source.LastIndexOf("@if (!wallet.Archived)", paymentDefaultRow, StringComparison.Ordinal);

        paymentDefaultRow.Should().BeGreaterThan(0);
        guard.Should().BeGreaterThan(0, "the payment-default control should be inside an archived guard");
    }

    [Fact]
    public void Parses_numbers_and_dates_against_invariant_culture()
    {
        // MudNumericField and a MudTextField typed DateOnly? parse user input against the server's
        // own CurrentCulture unless told otherwise - on a ru-RU/sr-Latn-RS host, "1500.50" is not a
        // valid number (comma is the decimal separator there) and the field silently keeps 0.
        var source = SourceText();
        var occurrences = source.Split("Culture=\"@CultureInfo.InvariantCulture\"").Length - 1;

        occurrences.Should().BeGreaterThanOrEqualTo(10,
            "the opening balance and date, plus the rate and three fees of a terms row and of the new-terms row");
    }

    [Fact]
    public void Every_numeric_field_parses_against_invariant_culture()
    {
        // A rate typed as 117.35 on the operator's ru-RU or sr-Latn-RS Windows would otherwise be refused or read as
        // 11735 (review focus 4) - each field is checked on its own, so a new one cannot hide behind the count above.
        var fields = NumericFields();

        fields.Should().HaveCount(9,
            "the opening balance, plus the rate and three fees of a terms row and of the new-terms row");
        fields.Should().AllSatisfy(field => field.Should().Contain("Culture=\"@CultureInfo.InvariantCulture\""));
    }

    [Fact]
    public void Every_numeric_field_reads_a_decimal_comma_and_reports_what_it_cannot_read()
    {
        // MudBlazor's default converter reads "117,35" as 11735 under the invariant culture and an unreadable value as
        // null (amendment 27); each field carries its own converter instance, because the instance remembers that its
        // field failed - shared, one field's good value would clear another's refusal.
        var fields = NumericFields();
        var converters = fields
            .Select(field => Regex.Match(field, @"Converter=""@(?<name>[A-Za-z_][\w.]*)"""))
            .ToList();

        fields.Should().NotBeEmpty();
        fields.Should().AllSatisfy(field => field.Should().NotContain("Converter=\"@(new "));
        converters.Should().AllSatisfy(match => match.Success.Should().BeTrue());
        converters.Select(match => match.Groups["name"].Value).Should().OnlyHaveUniqueItems();
    }
}
