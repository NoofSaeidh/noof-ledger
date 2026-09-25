using AwesomeAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Host.Diagnostics;

namespace Noof.Ledger.Host.Tests.Diagnostics;

public class LogSinkHealthCheckTests
{
    static readonly DateTimeOffset T0 = new(2026, 9, 25, 3, 0, 0, TimeSpan.Zero);

    static IDatabaseGate ReadyGate()
    {
        var gate = Substitute.For<IDatabaseGate>();
        gate.State.Returns(DatabaseState.Ready);
        return gate;
    }

    static ILogSinkStatus StatusWith(DateTimeOffset? lastFailureAt)
    {
        var status = Substitute.For<ILogSinkStatus>();
        status.LastFailureAt.Returns(lastFailureAt);
        return status;
    }

    [Fact]
    public async Task No_failure_ever_is_healthy()
    {
        var check = new LogSinkHealthCheck(ReadyGate(), StatusWith(null), new FakeTimeProvider(T0));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task A_failure_within_ten_minutes_is_degraded()
    {
        var check = new LogSinkHealthCheck(ReadyGate(), StatusWith(T0.AddMinutes(-5)), new FakeTimeProvider(T0));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Be("Logs are in the file only");
    }

    [Fact]
    public async Task A_failure_more_than_ten_minutes_ago_is_healthy_again()
    {
        var check = new LogSinkHealthCheck(ReadyGate(), StatusWith(T0.AddMinutes(-11)), new FakeTimeProvider(T0));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Reports_degraded_for_the_gate_before_looking_at_the_sink()
    {
        var gate = Substitute.For<IDatabaseGate>();
        gate.State.Returns(DatabaseState.Waiting);
        var check = new LogSinkHealthCheck(gate, StatusWith(T0), new FakeTimeProvider(T0));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Description.Should().Be("Waiting for the database");
    }
}
