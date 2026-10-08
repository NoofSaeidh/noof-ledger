using AwesomeAssertions;
using Noof.Ledger.Application.Diagnostics.BugReports;
using Noof.Ledger.Host.Workers;

namespace Noof.Ledger.Host.Tests;

public class BugReportRepliesTests
{
    static BugReportDelivery Delivery(
        BugExplanationState state = BugExplanationState.Done, string explanation = "Reply to the echo with the amount.",
        bool linked = true, int? findingsCount = 1) =>
        new(Guid.NewGuid(), 4, BugReportSource.Telegram, "reply-to-4", state,
            state == BugExplanationState.Done ? explanation : null,
            state == BugExplanationState.Done ? false : null,
            linked, findingsCount);

    [Fact]
    public void An_explained_report_answers_with_the_explanation_and_its_findings_line()
    {
        BugReportReplies.Compose(Delivery()).Should().Be("Reply to the echo with the amount.\n\n1 finding on this record");
    }

    [Fact]
    public void A_report_that_could_not_be_explained_says_it_is_saved_and_nothing_more()
    {
        BugReportReplies.Compose(Delivery(BugExplanationState.Failed)).Should().Be("Couldn't explain it — the report is saved.");
    }

    [Theory]
    [InlineData(BugExplanationState.Pending)]
    [InlineData(BugExplanationState.Unknown)]
    public void A_report_not_answered_yet_or_in_no_state_at_all_has_no_reply(BugExplanationState state)
    {
        var act = () => BugReportReplies.Compose(Delivery(state));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(null, true, "Integrity findings could not be collected")]
    [InlineData(null, false, "Integrity findings could not be collected")]
    [InlineData(0, true, "No integrity findings")]
    [InlineData(0, false, "No integrity findings")]
    [InlineData(1, true, "1 finding on this record")]
    [InlineData(2, true, "2 findings on this record")]
    [InlineData(1, false, "1 finding in the ledger")]
    [InlineData(20, false, "20 findings in the ledger")]
    public void The_findings_line_says_how_many_and_where(int? findingsCount, bool linked, string expected)
    {
        BugReportReplies.FindingsLine(Delivery(linked: linked, findingsCount: findingsCount)).Should().Be(expected);
    }

    [Fact]
    public void An_explanation_over_the_limit_is_cut_to_it_so_telegram_never_refuses_the_reply()
    {
        var reply = BugReportReplies.Compose(Delivery(explanation: new string('a', 5000)));

        var (explanation, findingsLine) = (reply[..reply.IndexOf("\n\n", StringComparison.Ordinal)], reply[(reply.IndexOf("\n\n", StringComparison.Ordinal) + 2)..]);
        explanation.Should().HaveLength(BugReportReplies.MaxExplanationLength).And.EndWith("…").And.StartWith(new string('a', 3499));
        findingsLine.Should().Be("1 finding on this record");
    }

    [Fact]
    public void An_explanation_exactly_at_the_limit_is_kept_whole()
    {
        var atLimit = new string('a', BugReportReplies.MaxExplanationLength);

        BugReportReplies.Compose(Delivery(explanation: atLimit)).Should().Be(atLimit + "\n\n1 finding on this record");
    }
}
