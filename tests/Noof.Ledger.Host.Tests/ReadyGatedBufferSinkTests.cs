using AwesomeAssertions;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Host.Logging;
using Serilog.Events;
using Serilog.Parsing;

namespace Noof.Ledger.Host.Tests;

public class ReadyGatedBufferSinkTests
{
    [Fact]
    public void Events_are_buffered_while_the_gate_is_not_Ready_and_nothing_reaches_the_inner_sink()
    {
        var gate = new FakeGate();
        var inner = new CollectingSink();
        var sink = new ReadyGatedBufferSink(inner, gate);

        sink.Emit(Event("one"));
        sink.Emit(Event("two"));

        inner.Events.Should().BeEmpty();
    }

    [Fact]
    public void Once_Ready_the_buffered_events_flush_in_order_before_a_new_event()
    {
        var gate = new FakeGate();
        var inner = new CollectingSink();
        var sink = new ReadyGatedBufferSink(inner, gate);

        sink.Emit(Event("one"));
        sink.Emit(Event("two"));

        gate.State = DatabaseState.Ready;
        sink.Emit(Event("three"));

        inner.Events.Select(e => e.MessageTemplate.Text).Should().Equal("one", "two", "three");
    }

    [Fact]
    public void Passes_events_straight_through_once_flushed()
    {
        var gate = new FakeGate { State = DatabaseState.Ready };
        var inner = new CollectingSink();
        var sink = new ReadyGatedBufferSink(inner, gate);

        sink.Emit(Event("only"));

        inner.Events.Should().ContainSingle().Which.MessageTemplate.Text.Should().Be("only");
    }

    [Fact]
    public void Beyond_capacity_the_oldest_buffered_event_is_dropped_and_counted()
    {
        var gate = new FakeGate();
        var inner = new CollectingSink();
        var sink = new ReadyGatedBufferSink(inner, gate, capacity: 3);

        sink.Emit(Event("one"));
        sink.Emit(Event("two"));
        sink.Emit(Event("three"));
        sink.Emit(Event("four"));

        gate.State = DatabaseState.Ready;
        sink.Emit(Event("five"));

        inner.Events.Select(e => e.MessageTemplate.Text).Should().Equal("two", "three", "four", "1 log events from before the database was ready were dropped", "five");
    }

    [Fact]
    public void No_warning_is_emitted_when_nothing_was_dropped()
    {
        var gate = new FakeGate();
        var inner = new CollectingSink();
        var sink = new ReadyGatedBufferSink(inner, gate, capacity: 10);

        sink.Emit(Event("one"));
        gate.State = DatabaseState.Ready;
        sink.Emit(Event("two"));

        inner.Events.Select(e => e.MessageTemplate.Text).Should().Equal("one", "two");
    }

    [Fact]
    public async Task Concurrent_emits_across_the_Ready_transition_never_throw_or_lose_events()
    {
        var gate = new FakeGate();
        var inner = new CollectingSink();
        var sink = new ReadyGatedBufferSink(inner, gate, capacity: 10_000);

        var writers = Enumerable.Range(0, 8).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
                sink.Emit(Event($"w{w}-{i}"));
        })).ToArray();

        await Task.Delay(20, TestContext.Current.CancellationToken);
        gate.State = DatabaseState.Ready;

        await Task.WhenAll(writers);
        sink.Emit(Event("final"));

        inner.Events.Select(e => e.MessageTemplate.Text).Should().Contain("final");
    }

    static LogEvent Event(string message) => new(
        DateTimeOffset.UtcNow, LogEventLevel.Information, null,
        new MessageTemplateParser().Parse(message), []);

    sealed class FakeGate : IDatabaseGate
    {
        readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DatabaseState state = DatabaseState.Waiting;

        public DatabaseState State
        {
            get => state;
            set
            {
                state = value;
                if (value == DatabaseState.Ready)
                    ready.TrySetResult();
            }
        }

        public string? Detail => null;

        public Task WaitUntilReadyAsync(CancellationToken cancellationToken) => ready.Task;
    }

    sealed class CollectingSink : Serilog.Core.ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
