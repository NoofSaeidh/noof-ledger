using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

public class IntegrityPageSourceTests
{
    static readonly string[] PopoverComponents =
        ["MudSelect", "MudDatePicker", "MudAutocomplete", "MudMenu", "MudTooltip", "MudDialog", "MudSnackbar"];

    static readonly string[] Markers =
    [
        "\"integrity-bugs\"", "\"integrity-waiting\"", "id=\"integrity-empty\"", "id=\"integrity-error\"",
        "finding-{i}", "finding-{i}-link", "explain-{i}", "explanation-{i}", "create-{i}", "dismiss-{i}", "created-{i}",
    ];

    static string Read(params string[] pathSegments) => File.ReadAllText(Path.Combine(
        [RepoRoot.Find().FullName, "src", "Noof.Ledger.Web", "Components", .. pathSegments]));

    static string PageSource() => Read("Pages", "DiagnosticsIntegrity.razor");

    [Fact]
    public void Integrity_page_is_routable_authorised_interactive_and_cancels_on_dispose()
    {
        var source = PageSource();

        source.Should().Contain("@page \"/diagnostics/integrity\"");
        source.Should().Contain("[Authorize]");
        source.Should().Contain("InteractiveServerRenderMode(prerender: false)");
        source.Should().Contain("@implements IDisposable");
        source.Should().Contain("CancellationTokenSource");
        source.Should().NotContain("CancellationToken.None",
            "navigating away must cancel a running check or explanation through the page's own token");
        source.Should().Contain("IDatabaseGate");
        source.Should().Contain("DatabaseGateBanner");
        foreach (var component in PopoverComponents)
            source.Should().NotContain(component, "the layout renders statically, so feedback is an inline MudAlert");
    }

    [Fact]
    public void Integrity_page_runs_the_checks_through_the_flows_gate_and_leaves_Explain_and_Create_to_the_flow()
    {
        var source = PageSource();

        source.Should().Contain("IIntegrityChecks");
        source.Should().Contain("FindAllAsync(");
        source.Should().Contain("RunExclusiveAsync(",
            "the circuit has one LedgerDbContext: the load shares the one-at-a-time gate with Explain and Create");
        source.Should().Contain("FindingExplanationFlow");
        source.Should().NotContain("CreateFromDashboardAsync(",
            "Create goes through FindingExplanationFlow, which Host.Tests drives (spec P-18)");
    }

    [Fact]
    public void Integrity_page_loads_its_findings_once_and_disables_every_action_while_one_runs()
    {
        var source = PageSource();

        Regex.Count(source, @"\bfindings\s*=[^=>]").Should().Be(1,
            "the flow keys its state by finding index, so a reload of the findings would show a stale answer under another finding");
        Regex.Count(source, @"RunExclusiveAsync\(").Should().Be(1,
            "the gate is not reentrant: a second, nested exclusive run waits on the first forever");
        Regex.Count(source, "Disabled=\"@Flow.Busy\"").Should().Be(Regex.Count(source, "<MudButton "),
            "a press while another action runs would queue behind it; every button waits for the one running");
    }

    [Fact]
    public void Integrity_page_carries_every_id_the_tests_and_the_screenshots_select_by()
    {
        var source = PageSource();

        foreach (var marker in Markers)
            source.Should().Contain(marker);
        source.Should().Contain("<FindingFacts ");
    }

    [Fact]
    public void A_findings_age_is_marked_as_a_live_value_for_the_screenshots_to_hide()
    {
        var facts = Read("Shared", "FindingFacts.razor");

        facts.Should().Contain("SinceFact");
        facts.Should().Contain("noof-live-value",
            "a finding's age follows the host's real clock; AppScreens.HideLiveValues hides it by this class (spec P-19)");
        facts.Should().Contain("FormatValue(");
    }
}
