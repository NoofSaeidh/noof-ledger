using AwesomeAssertions;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Host.Startup;

namespace Noof.Ledger.Host.Tests;

public class DatabaseGateTests
{
    [Fact]
    public void Starts_waiting_with_no_detail()
    {
        var gate = new DatabaseGate();

        gate.State.Should().Be(DatabaseState.Waiting);
        gate.Detail.Should().BeNull();
    }

    [Fact]
    public void Set_updates_the_state_and_the_detail()
    {
        var gate = new DatabaseGate();

        gate.Set(DatabaseState.Failed, "the database rejected the migration");

        gate.State.Should().Be(DatabaseState.Failed);
        gate.Detail.Should().Be("the database rejected the migration");
    }

    [Fact]
    public async Task WaitUntilReadyAsync_completes_immediately_once_the_gate_is_already_ready()
    {
        var gate = new DatabaseGate();
        gate.Set(DatabaseState.Ready, null);

        var waiting = gate.WaitUntilReadyAsync(TestContext.Current.CancellationToken);

        waiting.IsCompletedSuccessfully.Should().BeTrue();
        await waiting;
    }

    [Fact]
    public async Task WaitUntilReadyAsync_completes_once_the_gate_becomes_ready()
    {
        var gate = new DatabaseGate();

        var waiting = gate.WaitUntilReadyAsync(TestContext.Current.CancellationToken);
        waiting.IsCompleted.Should().BeFalse();

        gate.Set(DatabaseState.Ready, null);

        await waiting;
    }

    [Fact]
    public async Task WaitUntilReadyAsync_honours_cancellation_while_the_gate_never_becomes_ready()
    {
        var gate = new DatabaseGate();
        using var cts = new CancellationTokenSource();

        var waiting = gate.WaitUntilReadyAsync(cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(() => waiting);
    }
}
