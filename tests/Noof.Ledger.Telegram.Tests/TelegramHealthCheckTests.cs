using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Telegram.Diagnostics;

namespace Noof.Ledger.Telegram.Tests;

public class TelegramHealthCheckTests
{
    static readonly DateTimeOffset T0 = new(2026, 9, 25, 3, 0, 0, TimeSpan.Zero);

    static IDatabaseGate ReadyGate()
    {
        var gate = Substitute.For<IDatabaseGate>();
        gate.State.Returns(DatabaseState.Ready);
        return gate;
    }

    static ISecretStore StoreWithToken(SecretState state = SecretState.Present)
    {
        var store = Substitute.For<ISecretStore>();
        store.GetStatusAsync(SecretKeys.TelegramBotToken, Arg.Any<CancellationToken>())
            .Returns(new SecretStatus(state, null));
        return store;
    }

    static TelegramHealthCheck Check(IPollingHeartbeat heartbeat, FakeTimeProvider time, IDatabaseGate? gate = null, ISecretStore? store = null) =>
        new(gate ?? ReadyGate(), store ?? StoreWithToken(), heartbeat, time);

    [Fact]
    public void Reports_its_name_order_and_log_category()
    {
        var check = Check(Substitute.For<IPollingHeartbeat>(), new FakeTimeProvider(T0));

        check.Name.Should().Be("Telegram");
        check.Order.Should().Be(30);
        check.LogCategory.Should().Be("Noof.Ledger.Telegram");
    }

    [Fact]
    public async Task Reports_a_warning_when_the_gate_is_not_ready()
    {
        var gate = Substitute.For<IDatabaseGate>();
        gate.State.Returns(DatabaseState.Waiting);
        var check = Check(Substitute.For<IPollingHeartbeat>(), new FakeTimeProvider(T0), gate: gate);

        var result = await check.CheckAsync(TestContext.Current.CancellationToken);

        result.Level.Should().Be(HealthLevel.Warning);
        result.Summary.Should().Be("Waiting for the database");
    }

    [Fact]
    public async Task Reports_failing_when_the_token_secret_is_missing()
    {
        var check = Check(Substitute.For<IPollingHeartbeat>(), new FakeTimeProvider(T0), store: StoreWithToken(SecretState.Missing));

        var result = await check.CheckAsync(TestContext.Current.CancellationToken);

        result.Level.Should().Be(HealthLevel.Failing);
    }

    [Fact]
    public async Task Reports_ok_when_the_last_success_is_within_two_minutes()
    {
        var heartbeat = Substitute.For<IPollingHeartbeat>();
        heartbeat.LastSuccessAt.Returns(T0.AddMinutes(-1));
        var check = Check(heartbeat, new FakeTimeProvider(T0));

        var result = await check.CheckAsync(TestContext.Current.CancellationToken);

        result.Level.Should().Be(HealthLevel.Ok);
    }

    [Fact]
    public async Task A_network_failure_is_a_warning_and_says_this_is_normal()
    {
        var heartbeat = Substitute.For<IPollingHeartbeat>();
        heartbeat.LastFailure.Returns((T0, PollFailure.Network));
        var check = Check(heartbeat, new FakeTimeProvider(T0));

        var result = await check.CheckAsync(TestContext.Current.CancellationToken);

        result.Level.Should().Be(HealthLevel.Warning);
        result.Summary.Should().Be("Offline — this is normal");
    }

    [Fact]
    public async Task An_unauthorized_failure_is_failing_and_names_the_rejected_token()
    {
        var heartbeat = Substitute.For<IPollingHeartbeat>();
        heartbeat.LastFailure.Returns((T0, PollFailure.Unauthorized));
        var check = Check(heartbeat, new FakeTimeProvider(T0));

        var result = await check.CheckAsync(TestContext.Current.CancellationToken);

        result.Level.Should().Be(HealthLevel.Failing);
        result.Summary.Should().Be("Telegram rejected the bot token");
    }

    [Fact]
    public async Task No_poll_yet_is_a_warning_and_says_so()
    {
        var check = Check(Substitute.For<IPollingHeartbeat>(), new FakeTimeProvider(T0));

        var result = await check.CheckAsync(TestContext.Current.CancellationToken);

        result.Level.Should().Be(HealthLevel.Warning);
        result.Summary.Should().Be("Waiting for the first poll");
    }
}
