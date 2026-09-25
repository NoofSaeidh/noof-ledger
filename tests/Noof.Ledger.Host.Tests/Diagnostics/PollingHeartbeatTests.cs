using AwesomeAssertions;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Host.Diagnostics;

namespace Noof.Ledger.Host.Tests.Diagnostics;

public class PollingHeartbeatTests
{
    static readonly DateTimeOffset T0 = new(2026, 9, 25, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Starts_with_no_success_and_no_failure()
    {
        var heartbeat = new PollingHeartbeat();

        heartbeat.LastSuccessAt.Should().BeNull();
        heartbeat.LastFailure.Should().BeNull();
    }

    [Fact]
    public void Records_a_success_and_clears_a_prior_failure()
    {
        var heartbeat = new PollingHeartbeat();
        heartbeat.RecordFailure(T0, PollFailure.Network);

        heartbeat.RecordSuccess(T0.AddMinutes(1));

        heartbeat.LastSuccessAt.Should().Be(T0.AddMinutes(1));
        heartbeat.LastFailure.Should().BeNull();
    }

    [Fact]
    public void Records_a_failure_with_its_kind_without_touching_the_last_success()
    {
        var heartbeat = new PollingHeartbeat();
        heartbeat.RecordSuccess(T0);

        heartbeat.RecordFailure(T0.AddMinutes(5), PollFailure.Unauthorized);

        heartbeat.LastSuccessAt.Should().Be(T0);
        heartbeat.LastFailure.Should().Be((T0.AddMinutes(5), PollFailure.Unauthorized));
    }
}
