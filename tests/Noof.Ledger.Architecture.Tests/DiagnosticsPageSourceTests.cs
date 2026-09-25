using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

public class DiagnosticsPageSourceTests
{
    static string SourceText(string fileName) => File.ReadAllText(Path.Combine(
        RepoRoot.Find().FullName, "src", "Noof.Ledger.Web", "Components", "Pages", fileName));

    [Fact]
    public void Diagnostics_page_is_routable_and_authorised_and_reads_ISystemHealth_only()
    {
        var source = SourceText("Diagnostics.razor");

        source.Should().Contain("@page \"/diagnostics\"");
        source.Should().Contain("[Authorize]");
        source.Should().Contain("ISystemHealth");
        source.Should().Contain("id=\"diagnostics-checks\"");
        source.Should().Contain("HealthCheckLogCategories",
            "the Logs link must open the check's owning logger category, not the check's display name (I-1)");
        source.Should().Contain("id=\"diagnostics-view-all-logs\"");
        source.Should().NotContain("diagnostics-transaction-id",
            "the confusing transaction-id lookup form was removed - the trace is reachable from Transactions and log rows instead");
        source.Should().NotContain("MudSelect");
        source.Should().NotContain("MudDatePicker");
        source.Should().NotContain("MudAutocomplete");
        source.Should().NotContain("MudMenu");
        source.Should().NotContain("MudTooltip");
        source.Should().NotContain("MudDialog");
        source.Should().NotContain("MudSnackbar");
    }

    [Fact]
    public void Logs_page_is_routable_authorised_interactive_and_paged_without_virtualize()
    {
        var source = SourceText("DiagnosticsLogs.razor");

        source.Should().Contain("@page \"/diagnostics/logs\"");
        source.Should().Contain("[Authorize]");
        source.Should().Contain("InteractiveServerRenderMode(prerender: false)");
        source.Should().Contain("ILogQuery");
        source.Should().Contain("ILogFileTail");
        source.Should().Contain("id=\"logs-grid\"");
        source.Should().Contain("id=\"logs-file-tail\"");
        source.Should().Contain("id=\"logs-filter-from\"",
            "LogFilter carries From/To (binding spec §4) - the UI must expose both, not just MinLevel/Text/Source/TransactionId");
        source.Should().Contain("id=\"logs-filter-to\"");
        source.Should().Contain("id=\"logs-quick-range\"",
            "Task 9: quick date ranges (Last hour/Today/7 days/All) alongside the custom From/To fields");
        source.Should().Contain("id=\"logs-sort-order\"",
            "Task 9: a sort toggle between newest-first and oldest-first");
        source.Should().Contain("""Typo="Typo.caption" Class="mud-text-secondary">Level""",
            "the Level select must carry the same caption-above-the-control label every other filter field does");
        source.Should().Contain("placeholder=\"e.g. 3a70ddca-2352-",
            "a transaction id is a GUID from the trace page - the field must show what one looks like");
        source.Should().Contain("[SupplyParameterFromQuery]",
            "/diagnostics's per-check Logs link navigates to /diagnostics/logs?source=<name> - this page must read that query parameter to seed the filter");
        source.Should().NotContain("Virtualize",
            "the spec is explicit: paged QuickGrid, no Virtualize (§4)");
        source.Should().NotContain("MudSelect");
        source.Should().NotContain("MudDatePicker");
        source.Should().NotContain("MudAutocomplete");
        source.Should().NotContain("MudMenu");
        source.Should().NotContain("MudTooltip");
        source.Should().NotContain("MudDialog");
        source.Should().NotContain("MudSnackbar");
    }

    [Fact]
    public void Trace_page_is_routable_authorised_and_reads_ITransactionTrace_only()
    {
        var source = SourceText("TransactionTrace.razor");

        source.Should().Contain("@page \"/transactions/{Id:guid}/trace\"");
        source.Should().Contain("[Authorize]");
        source.Should().Contain("ITransactionTrace");
        source.Should().Contain("id=\"trace-summary\"");
        source.Should().Contain("id=\"trace-stages\"");
        source.Should().Contain("id=\"trace-timeline\"");
        source.Should().Contain("id=\"trace-history\"");
        source.Should().NotContain("MudSelect");
        source.Should().NotContain("MudDatePicker");
        source.Should().NotContain("MudAutocomplete");
        source.Should().NotContain("MudMenu");
        source.Should().NotContain("MudTooltip");
        source.Should().NotContain("MudDialog");
        source.Should().NotContain("MudSnackbar");
    }
}
