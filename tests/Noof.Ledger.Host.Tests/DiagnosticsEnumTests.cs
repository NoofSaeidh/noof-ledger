using AwesomeAssertions;
using Noof.Ledger.Application.Diagnostics.BugReports;
using Noof.Ledger.Application.Diagnostics.Integrity;

namespace Noof.Ledger.Host.Tests;

public class DiagnosticsEnumTests
{
    [Fact]
    public void Integrity_check_values_never_move()
    {
        ((int)IntegrityCheck.Unknown).Should().Be(0);
        ((int)IntegrityCheck.PostingsDisagree).Should().Be(1);
        ((int)IntegrityCheck.FactsMismatchKind).Should().Be(2);
        ((int)IntegrityCheck.StuckInPipeline).Should().Be(3);
        ((int)IntegrityCheck.NotApplied).Should().Be(4);
        Enum.GetValues<IntegrityCheck>().Should().HaveCount(5);
    }

    [Fact]
    public void Integrity_group_values_never_move()
    {
        ((int)IntegrityGroup.Unknown).Should().Be(0);
        ((int)IntegrityGroup.Bug).Should().Be(1);
        ((int)IntegrityGroup.WaitingOnYou).Should().Be(2);
        Enum.GetValues<IntegrityGroup>().Should().HaveCount(3);
    }

    [Fact]
    public void Bug_report_source_values_never_move()
    {
        ((int)BugReportSource.Unknown).Should().Be(0);
        ((int)BugReportSource.Telegram).Should().Be(1);
        ((int)BugReportSource.Dashboard).Should().Be(2);
        Enum.GetValues<BugReportSource>().Should().HaveCount(3);
    }

    [Fact]
    public void Bug_report_status_values_never_move()
    {
        ((int)BugReportStatus.Unknown).Should().Be(0);
        ((int)BugReportStatus.Open).Should().Be(1);
        ((int)BugReportStatus.Closed).Should().Be(2);
        Enum.GetValues<BugReportStatus>().Should().HaveCount(3);
    }

    [Fact]
    public void Bug_explanation_state_values_never_move()
    {
        ((int)BugExplanationState.Unknown).Should().Be(0);
        ((int)BugExplanationState.Pending).Should().Be(1);
        ((int)BugExplanationState.Done).Should().Be(2);
        ((int)BugExplanationState.Failed).Should().Be(3);
        Enum.GetValues<BugExplanationState>().Should().HaveCount(4);
    }
}
