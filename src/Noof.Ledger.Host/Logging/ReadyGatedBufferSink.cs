using Noof.Ledger.Application.Diagnostics;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Noof.Ledger.Host.Logging;

// Replaces a Filter.ByIncludingOnly(_ => gate.State == Ready) sub-logger, which silently dropped
// every event logged before the database gate was Ready instead of ever delivering it - the log
// storage's whole point is a record of what happened, including startup and migration. This buffers
// up to `capacity` pre-Ready events (dropping the oldest past that) and flushes them, in order, the
// moment the gate turns Ready, before any event logged after that point.
internal sealed class ReadyGatedBufferSink : ILogEventSink, IDisposable
{
    static readonly MessageTemplateParser TemplateParser = new();

    readonly ILogEventSink inner;
    readonly IDatabaseGate gate;
    readonly ILogEventSink? fallback;
    readonly int capacity;
    readonly Lock gateLock = new();
    readonly Queue<LogEvent> buffer = new();
    readonly CancellationTokenSource disposed = new();
    int dropped;
    bool flushed;

    // fallback (M-9, Phase 5 final review): where Dispose reports events that never reached `inner`
    // because the gate never turned Ready - console/file in production (LoggingSetup wires it to the
    // same sub-logger `destinations`' Console+File sinks feed), never app_log, since there is no
    // database connection to write to at that point.
    public ReadyGatedBufferSink(ILogEventSink inner, IDatabaseGate gate, ILogEventSink? fallback = null, int capacity = 10_000)
    {
        this.inner = inner;
        this.gate = gate;
        this.fallback = fallback;
        this.capacity = capacity;

        // Emit() also checks gate.State on every call and flushes there if it finds Ready already,
        // but a quiet host - nothing else logged between Ready and here - would otherwise leave the
        // buffer sitting forever waiting for an Emit call that never comes. This is what makes
        // "flushes the moment the gate turns Ready" true regardless of what else is happening.
        _ = ObserveGateAsync();
    }

    async Task ObserveGateAsync()
    {
        try
        {
            await gate.WaitUntilReadyAsync(disposed.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (gateLock)
        {
            if (!flushed)
                FlushLocked();
        }
    }

    public void Emit(LogEvent logEvent)
    {
        lock (gateLock)
        {
            if (!flushed)
            {
                if (gate.State == DatabaseState.Ready)
                {
                    FlushLocked();
                }
                else
                {
                    if (buffer.Count >= capacity)
                    {
                        buffer.Dequeue();
                        dropped++;
                    }

                    buffer.Enqueue(logEvent);
                    return;
                }
            }
        }

        inner.Emit(logEvent);
    }

    void FlushLocked()
    {
        while (buffer.Count > 0)
            inner.Emit(buffer.Dequeue());

        if (dropped > 0)
        {
            inner.Emit(DroppedWarning(dropped));
            dropped = 0;
        }

        flushed = true;
    }

    static LogEvent DroppedWarning(int count) => new(
        DateTimeOffset.Now, LogEventLevel.Warning, null,
        TemplateParser.Parse($"{count} log events from before the database was ready were dropped"), []);

    static LogEvent NeverFlushedWarning(int count) => new(
        DateTimeOffset.Now, LogEventLevel.Warning, null,
        TemplateParser.Parse(
            $"{count} log events were never written to the database because it was never ready before shutdown; they are in the log file"),
        []);

    public void Dispose()
    {
        disposed.Cancel();
        disposed.Dispose();

        lock (gateLock)
        {
            if (!flushed)
            {
                var neverFlushed = buffer.Count + dropped;
                if (neverFlushed > 0)
                    fallback?.Emit(NeverFlushedWarning(neverFlushed));
            }
        }

        if (inner is IDisposable disposable)
            disposable.Dispose();
    }
}
