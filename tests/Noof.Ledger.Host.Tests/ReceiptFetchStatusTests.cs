using AwesomeAssertions;
using Noof.Ledger.Host.Diagnostics;

namespace Noof.Ledger.Host.Tests;

public class ReceiptFetchStatusTests
{
    [Fact]
    public void A_freshly_created_status_has_no_failure()
    {
        var status = new ReceiptFetchStatus();

        status.LastFailureAt.Should().BeNull();
        status.LastFailureReason.Should().BeNull();
    }

    [Fact]
    public void Recording_a_failure_is_read_back_exactly()
    {
        var status = new ReceiptFetchStatus();
        var at = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

        status.RecordFailure(at, "504 gateway timeout");

        status.LastFailureAt.Should().Be(at);
        status.LastFailureReason.Should().Be("504 gateway timeout");
    }

    [Fact]
    public void A_later_failure_overwrites_an_earlier_one()
    {
        var status = new ReceiptFetchStatus();
        var first = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        var second = first.AddMinutes(5);

        status.RecordFailure(first, "timeout");
        status.RecordFailure(second, "connection refused");

        status.LastFailureAt.Should().Be(second);
        status.LastFailureReason.Should().Be("connection refused");
    }
}
