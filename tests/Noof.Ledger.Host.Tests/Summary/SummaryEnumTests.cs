using AwesomeAssertions;
using Noof.Ledger.Application.Reporting.Summary;

namespace Noof.Ledger.Host.Tests.Summary;

// CLAUDE.md: a new enum's 0 is Unknown, never a real value, so a default is never mistaken for one.
public class SummaryEnumTests
{
    [Fact]
    public void Summary_line_kind_values_never_move()
    {
        ((int)SummaryLineKind.Unknown).Should().Be(0);
        ((int)SummaryLineKind.Spent).Should().Be(1);
        ((int)SummaryLineKind.Received).Should().Be(2);
        ((int)SummaryLineKind.Refund).Should().Be(3);
        Enum.GetValues<SummaryLineKind>().Should().HaveCount(4);
    }

    [Fact]
    public void Highlight_base_values_never_move()
    {
        ((int)HighlightBase.Unknown).Should().Be(0);
        ((int)HighlightBase.Average).Should().Be(1);
        ((int)HighlightBase.PreviousMonth).Should().Be(2);
        Enum.GetValues<HighlightBase>().Should().HaveCount(3);
    }
}
