using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Host.Diagnostics;

namespace Noof.Ledger.Host.Tests.Diagnostics;

public class ReceiptsHealthCheckTests
{
    static readonly DateTimeOffset T0 = new(2026, 9, 25, 3, 0, 0, TimeSpan.Zero);
    static readonly TimeZoneInfo Belgrade = TimeZoneInfo.FindSystemTimeZoneById("Europe/Belgrade");

    static IDatabaseGate ReadyGate()
    {
        var gate = Substitute.For<IDatabaseGate>();
        gate.State.Returns(DatabaseState.Ready);
        return gate;
    }

    static IReceiptFetchStatus StatusWith(DateTimeOffset? lastFailureAt)
    {
        var status = Substitute.For<IReceiptFetchStatus>();
        status.LastFailureAt.Returns(lastFailureAt);
        return status;
    }

    [Fact]
    public void Reports_its_name_order_and_log_category()
    {
        var check = new ReceiptsHealthCheck(ReadyGate(), StatusWith(null), new FakeTimeProvider(T0), Belgrade);

        check.Name.Should().Be("Receipts");
        check.Order.Should().Be(80);
        check.LogCategory.Should().Be("Noof.Ledger.Host.Workers.ExtractReceiptWorker");
    }

    [Fact]
    public async Task Reports_a_warning_when_the_gate_is_not_ready()
    {
        var gate = Substitute.For<IDatabaseGate>();
        gate.State.Returns(DatabaseState.Waiting);
        var check = new ReceiptsHealthCheck(gate, StatusWith(null), new FakeTimeProvider(T0), Belgrade);

        var result = await check.CheckAsync(TestContext.Current.CancellationToken);

        result.Level.Should().Be(HealthLevel.Warning);
        result.Summary.Should().Be("Waiting for the database");
    }

    [Fact]
    public async Task Never_having_failed_is_ok()
    {
        var check = new ReceiptsHealthCheck(ReadyGate(), StatusWith(null), new FakeTimeProvider(T0), Belgrade);

        var result = await check.CheckAsync(TestContext.Current.CancellationToken);

        result.Level.Should().Be(HealthLevel.Ok);
        result.Summary.Should().Be("No failed lookups in 24 h");
    }

    [Fact]
    public async Task A_failure_23_hours_ago_is_a_warning_naming_the_local_failure_time()
    {
        var failedAt = T0.AddHours(-23);
        var check = new ReceiptsHealthCheck(ReadyGate(), StatusWith(failedAt), new FakeTimeProvider(T0), Belgrade);

        var result = await check.CheckAsync(TestContext.Current.CancellationToken);

        result.Level.Should().Be(HealthLevel.Warning);
        var localHour = TimeZoneInfo.ConvertTime(failedAt, Belgrade).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        result.Summary.Should().Be($"Tax Administration unreachable at {localHour} — receipts fall back to the photo");
    }

    [Fact]
    public async Task A_failure_25_hours_ago_is_ok_again()
    {
        var check = new ReceiptsHealthCheck(ReadyGate(), StatusWith(T0.AddHours(-25)), new FakeTimeProvider(T0), Belgrade);

        var result = await check.CheckAsync(TestContext.Current.CancellationToken);

        result.Level.Should().Be(HealthLevel.Ok);
        result.Summary.Should().Be("No failed lookups in 24 h");
    }
}
