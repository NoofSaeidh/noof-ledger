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
    readonly int capacity;
    readonly Lock gateLock = new();
    readonly Queue<LogEvent> buffer = new();
    readonly CancellationTokenSource disposed = new();
    int dropped;
    bool flushed;

    public ReadyGatedBufferSink(ILogEventSink inner, IDatabaseGate gate, int capacity = 10_000)
    {
        this.inner = inner;
        this.gate = gate;
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

    public void Dispose()
    {
        disposed.Cancel();
        disposed.Dispose();

        if (inner is IDisposable disposable)
            disposable.Dispose();
    }
}
