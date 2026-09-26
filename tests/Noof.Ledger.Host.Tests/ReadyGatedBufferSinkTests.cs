using AwesomeAssertions;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Host.Logging;
using Serilog.Core;
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

    // M-9 (Phase 5 final review): if the gate ends Failed (or the host stops before Ready), buffered
    // events used to be dropped in Dispose with no trace at all - the only "N dropped" warning ever
    // existed on the flush path. The fallback sink (console/file in production, never app_log - there
    // is no database to write to) gets one warning line instead.
    [Fact]
    public void Disposing_while_the_gate_never_became_Ready_logs_one_warning_to_the_fallback_sink()
    {
        var gate = new FakeGate();
        var inner = new CollectingSink();
        var fallback = new CollectingSink();
        var sink = new ReadyGatedBufferSink(inner, gate, fallback, capacity: 10);

        sink.Emit(Event("one"));
        sink.Emit(Event("two"));

        sink.Dispose();

        fallback.Events.Should().ContainSingle();
        fallback.Events[0].Level.Should().Be(LogEventLevel.Warning);
        fallback.Events[0].RenderMessage().Should().Contain("2");
        inner.Events.Should().BeEmpty("nothing was ever flushed to the database sink");
    }

    [Fact]
    public void Disposing_after_a_clean_flush_logs_no_warning_to_the_fallback_sink()
    {
        var gate = new FakeGate { State = DatabaseState.Ready };
        var inner = new CollectingSink();
        var fallback = new CollectingSink();
        var sink = new ReadyGatedBufferSink(inner, gate, fallback);

        sink.Emit(Event("one"));
        sink.Dispose();

        fallback.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task A_level_loaded_during_the_Ready_transition_re_filters_events_already_buffered_under_the_earlier_default()
    {
        var gate = new FakeGate();
        var inner = new CollectingSink();
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);
        var sink = new ReadyGatedBufferSink(
            inner, gate,
            loadDatabaseLevelAsync: _ =>
            {
                levelSwitch.MinimumLevel = LogEventLevel.Warning;
                return Task.CompletedTask;
            },
            levelSwitch: levelSwitch);

        sink.Emit(Event("info", LogEventLevel.Information));
        sink.Emit(Event("warn", LogEventLevel.Warning));

        gate.State = DatabaseState.Ready;

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (inner.Events.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        inner.Events.Select(e => e.MessageTemplate.Text).Should().Equal(["warn"],
            "the level loaded while the gate turned Ready must apply retroactively to events " +
            "buffered earlier under the higher-admitting compiled-in default");
    }

    [Fact]
    public async Task A_load_that_never_completes_leaves_the_buffer_unflushed()
    {
        var gate = new FakeGate();
        var inner = new CollectingSink();
        var neverCompletes = new TaskCompletionSource();
        var sink = new ReadyGatedBufferSink(inner, gate, loadDatabaseLevelAsync: _ => neverCompletes.Task);

        sink.Emit(Event("one"));
        gate.State = DatabaseState.Ready;

        await Task.Delay(50, TestContext.Current.CancellationToken);

        inner.Events.Should().BeEmpty("the flush must wait for the level load to finish before releasing anything");
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

    static LogEvent Event(string message, LogEventLevel level = LogEventLevel.Information) => new(
        DateTimeOffset.UtcNow, level, null,
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
