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

    [Fact]
    public async Task Concurrent_writers_and_readers_never_observe_a_torn_value()
    {
        var status = new LogSinkStatus();
        var values = Enumerable.Range(0, 8)
            .Select(i => new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero).AddMinutes(i * 137))
            .ToArray();

        var tornValueSeen = 0;

        var writers = values.Select(value => Task.Run(() =>
        {
            for (var i = 0; i < 20_000; i++)
                status.RecordFailure(value);
        }));

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 20_000; i++)
            {
                if (status.LastFailureAt is { } at && !values.Contains(at))
                    Interlocked.Increment(ref tornValueSeen);
            }
        }));

        await Task.WhenAll(writers.Concat(readers));

        tornValueSeen.Should().Be(0);
    }
}
