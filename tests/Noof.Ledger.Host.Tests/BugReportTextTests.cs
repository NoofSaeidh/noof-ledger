using AwesomeAssertions;
using Noof.Ledger.Application.Diagnostics.BugReports;
using Noof.Ledger.Web;

namespace Noof.Ledger.Host.Tests;

// What /bugs and /bugs/{number} print for a report's source, status and explanation. R-1: Unknown is no report's value -
// bug_reports refuses 0 - so it is never printed as if it were one.
public class BugReportTextTests
{
    [Theory]
    [InlineData(BugReportSource.Telegram, "Telegram")]
    [InlineData(BugReportSource.Dashboard, "Dashboard")]
    public void A_source_reads_as_its_name(BugReportSource source, string text) =>
        BugReportText.Source(source).Should().Be(text);

    [Theory]
    [InlineData(BugReportStatus.Open, "Open")]
    [InlineData(BugReportStatus.Closed, "Closed")]
    public void A_status_reads_as_its_name(BugReportStatus status, string text) =>
        BugReportText.Status(status).Should().Be(text);

    [Theory]
    [InlineData(BugExplanationState.Pending, "Not explained yet.")]
    [InlineData(BugExplanationState.Failed, "Couldn't explain it.")]
    public void An_explanation_not_given_says_why(BugExplanationState state, string text) =>
        BugReportText.Explanation(state, explanation: null).Should().Be(text);

    [Fact]
    public void A_done_explanation_is_the_models_text() =>
        BugReportText.Explanation(BugExplanationState.Done, "Reply to the echo with the amount.")
            .Should().Be("Reply to the echo with the amount.");

    [Fact]
    public void Every_value_but_Unknown_has_its_text()
    {
        foreach (var source in Enum.GetValues<BugReportSource>().Where(value => value != BugReportSource.Unknown))
            BugReportText.Source(source).Should().NotBeNullOrWhiteSpace();
        foreach (var status in Enum.GetValues<BugReportStatus>().Where(value => value != BugReportStatus.Unknown))
            BugReportText.Status(status).Should().NotBeNullOrWhiteSpace();
        foreach (var state in Enum.GetValues<BugExplanationState>().Where(value => value != BugExplanationState.Unknown))
            BugReportText.Explanation(state, "Text.").Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Unknown_is_refused_rather_than_printed()
    {
        FluentActions.Invoking(() => BugReportText.Source(BugReportSource.Unknown))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => BugReportText.Status(BugReportStatus.Unknown))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => BugReportText.Explanation(BugExplanationState.Unknown, "Text."))
            .Should().Throw<ArgumentOutOfRangeException>();
    }
}
