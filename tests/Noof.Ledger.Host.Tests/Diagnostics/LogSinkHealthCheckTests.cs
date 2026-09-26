using AwesomeAssertions;
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
    public void Reports_its_name_order_and_log_category()
    {
        var check = new LogSinkHealthCheck(ReadyGate(), StatusWith(null), new FakeTimeProvider(T0));

        check.Name.Should().Be("Log sink");
        check.Order.Should().Be(70);
        check.LogCategory.Should().Be("Noof.Ledger.Host.Logging");
    }

    [Fact]
    public async Task No_failure_ever_is_ok()
    {
        var check = new LogSinkHealthCheck(ReadyGate(), StatusWith(null), new FakeTimeProvider(T0));

        var result = await check.CheckAsync(TestContext.Current.CancellationToken);

        result.Level.Should().Be(HealthLevel.Ok);
    }

    [Fact]
    public async Task A_failure_within_ten_minutes_is_a_warning()
    {
        var check = new LogSinkHealthCheck(ReadyGate(), StatusWith(T0.AddMinutes(-5)), new FakeTimeProvider(T0));

        var result = await check.CheckAsync(TestContext.Current.CancellationToken);

        result.Level.Should().Be(HealthLevel.Warning);
        result.Summary.Should().Be("Logs are in the file only");
    }

    [Fact]
    public async Task A_failure_more_than_ten_minutes_ago_is_ok_again()
    {
        var check = new LogSinkHealthCheck(ReadyGate(), StatusWith(T0.AddMinutes(-11)), new FakeTimeProvider(T0));

        var result = await check.CheckAsync(TestContext.Current.CancellationToken);

        result.Level.Should().Be(HealthLevel.Ok);
    }

    [Fact]
    public async Task Reports_a_warning_for_the_gate_before_looking_at_the_sink()
    {
        var gate = Substitute.For<IDatabaseGate>();
        gate.State.Returns(DatabaseState.Waiting);
        var check = new LogSinkHealthCheck(gate, StatusWith(T0), new FakeTimeProvider(T0));

        var result = await check.CheckAsync(TestContext.Current.CancellationToken);

        result.Summary.Should().Be("Waiting for the database");
    }
}
