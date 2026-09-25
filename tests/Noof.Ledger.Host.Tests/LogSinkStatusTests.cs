using AwesomeAssertions;
using Noof.Ledger.Host.Diagnostics;

namespace Noof.Ledger.Host.Tests;

public class LogSinkStatusTests
{
    [Fact]
    public void A_freshly_created_status_has_no_failure()
    {
        var status = new LogSinkStatus();

        status.LastFailureAt.Should().BeNull();
    }

    [Fact]
    public void Recording_a_failure_is_read_back_exactly()
    {
        var status = new LogSinkStatus();
        var at = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

        status.RecordFailure(at);

        status.LastFailureAt.Should().Be(at);
    }

    [Fact]
    public void A_later_failure_overwrites_an_earlier_one()
    {
        var status = new LogSinkStatus();
        var first = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        var second = first.AddMinutes(5);

        status.RecordFailure(first);
        status.RecordFailure(second);

        status.LastFailureAt.Should().Be(second);
    }
}
