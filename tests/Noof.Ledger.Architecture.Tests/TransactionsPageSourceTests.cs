using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

public class TransactionsPageSourceTests
{
    static string SourceText() => File.ReadAllText(Path.Combine(
        RepoRoot.Find().FullName, "src", "Noof.Ledger.Web", "Components", "Pages", "Transactions.razor"));

    [Fact]
    public void Transactions_page_is_routable_authorised_interactive_and_paged_without_virtualize()
    {
        var source = SourceText();

        source.Should().Contain("@page \"/transactions\"");
        source.Should().Contain("[Authorize]");
        source.Should().Contain("InteractiveServerRenderMode(prerender: false)");
        source.Should().Contain("ITransactionList");
        source.Should().Contain("id=\"transactions-grid\"");
        source.Should().Contain("id=\"transactions-filters\"");
        source.Should().Contain("/trace",
            "every row must link to its transaction's trace page");
        source.Should().NotContain("Virtualize",
            "the spec is explicit: paged QuickGrid, no Virtualize");
        source.Should().NotContain("MudSelect");
        source.Should().NotContain("MudDatePicker");
        source.Should().NotContain("MudAutocomplete");
        source.Should().NotContain("MudMenu");
        source.Should().NotContain("MudTooltip");
        source.Should().NotContain("MudDialog");
        source.Should().NotContain("MudSnackbar");
    }

    // M-4 (Phase 5 final review): DateTime.Today is the server's own zone and bypasses TimeProvider -
    // OccurredOn is a local date in the capture zone (Capture:TimeZone), not the server's.
    [Fact]
    public void Quick_ranges_use_TimeProvider_and_the_capture_time_zone_not_DateTime_Today()
    {
        var source = SourceText();

        source.Should().NotContain("DateTime.Today");
        source.Should().Contain("TimeProvider.GetUtcNow()");
        source.Should().Contain("CaptureTimeZone");
    }

    [Fact]
    public void Reads_transactions_through_the_list_read_model_only()
    {
        SourceText().Should().Contain("ITransactionList");
    }
}
