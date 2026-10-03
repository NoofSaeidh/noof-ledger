using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

public class BugReportPagesSourceTests
{
    static readonly string[] PopoverComponents =
        ["MudSelect", "MudDatePicker", "MudAutocomplete", "MudMenu", "MudTooltip", "MudDialog", "MudSnackbar"];

    static readonly string[] ListIds = ["bugs-filter-open", "bugs-filter-all", "bugs-download", "bugs-list", "bugs-empty", "bugs-error"];

    static readonly string[] ReportIds =
    [
        "bug-report-missing", "bug-report-meta", "bug-report-text", "bug-report-trace-link", "bug-report-explanation",
        "bug-report-findings-then", "bug-report-findings-now", "bug-report-record", "bug-report-not-collected",
        "bug-report-log-lines", "bug-report-close", "bug-report-reopen", "bug-report-copy", "bug-report-copy-warning",
        "bug-report-copied", "bug-report-error",
    ];

    static string Page(string fileName) => File.ReadAllText(Path.Combine(
        RepoRoot.Find().FullName, "src", "Noof.Ledger.Web", "Components", "Pages", fileName));

    [Theory]
    [InlineData("BugReports.razor")]
    [InlineData("BugReport.razor")]
    public void Each_page_is_authorised_interactive_without_prerendering_one_action_at_a_time_and_cancels_on_dispose(string fileName)
    {
        var source = Page(fileName);

        source.Should().Contain("[Authorize]");
        source.Should().Contain("InteractiveServerRenderMode(prerender: false)");
        source.Should().Contain("@implements IDisposable");
        source.Should().Contain("CancellationTokenSource");
        source.Should().NotContain("CancellationToken.None");
        source.Should().Contain("IDatabaseGate");
        source.Should().Contain("DatabaseGateBanner");
        source.Should().Contain("OneAtATime", "the circuit shares one LedgerDbContext");
        foreach (var component in PopoverComponents)
            source.Should().NotContain(component, "the layout renders statically, so feedback is an inline MudAlert");
    }

    [Fact]
    public void The_list_filters_by_query_and_downloads_a_string_through_the_script_not_a_stream()
    {
        var source = Page("BugReports.razor");

        source.Should().Contain("@page \"/bugs\"");
        source.Should().Contain("[SupplyParameterFromQuery]");
        source.Should().Contain("public string? Status");
        source.Should().Contain("/bugs?status=all");
        foreach (var id in ListIds)
            source.Should().Contain($"id=\"{id}\"");
        source.Should().Contain("bug-{report.Number}");
        source.Should().Contain("bug-{report.Number}-link", "every actionable element has an id");
        source.Should().Contain("bug-{report.Number}-trace-link", "every actionable element has an id");
        source.Should().Contain("\"noofLedger.downloadText\"");
        source.Should().Contain("LoadForExportAsync(includeClosed: false");
        source.Should().NotContain("DotNetStreamReference",
            "the Markdown goes to download.js as a string; Web reads through Application and never names a stream");
    }

    [Fact]
    public void The_report_page_shows_every_section_and_copies_through_the_component_library()
    {
        var source = Page("BugReport.razor");

        source.Should().Contain("@page \"/bugs/{Number:int}\"");
        foreach (var id in ReportIds)
            source.Should().Contain($"id=\"{id}\"");
        source.Should().Contain("CopyToClipboardAsync(", "MudBlazor's IJsApiService needs no script of ours");
        source.Should().Contain("Contains your data — never paste it into a public issue.");
        source.Should().Contain("<FindingFacts ");
    }
}
