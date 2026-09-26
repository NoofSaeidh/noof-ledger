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
    static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(5);

    readonly ILogEventSink inner;
    readonly IDatabaseGate gate;
    readonly ILogEventSink? fallback;
    readonly Func<CancellationToken, Task>? loadDatabaseLevelAsync;
    readonly LoggingLevelSwitch? levelSwitch;
    readonly int capacity;
    readonly Lock gateLock = new();
    readonly Queue<LogEvent> buffer = new();
    readonly CancellationTokenSource disposed = new();
    int dropped;
    bool flushed;
    bool levelLoaded;

    // fallback (M-9, Phase 5 final review): where Dispose reports events that never reached `inner`
    // because the gate never turned Ready - console/file in production (LoggingSetup wires it to the
    // same sub-logger `destinations`' Console+File sinks feed), never app_log, since there is no
    // database connection to write to at that point.
    //
    // loadDatabaseLevelAsync/levelSwitch (major finding, Phase 5 final review): every event admitted
    // into `buffer` was already filtered once, by the outer WriteTo.Sink's own levelSwitch, but that
    // filter ran with whatever the switch's *pre-load* value was (the compiled-in default), because
    // the stored database log level is only read once the gate turns Ready - the same signal this
    // sink itself waits on. Without re-checking, a stored Off (or Warning/Error/Fatal) never governed
    // the startup burst on any restart: it was already inside the queue by the time the level loaded.
    // loadDatabaseLevelAsync lets the flush wait for that load first; levelSwitch lets FlushLocked
    // re-filter each buffered event against the now-current value before it ever reaches `inner`.
    public ReadyGatedBufferSink(
        ILogEventSink inner, IDatabaseGate gate, ILogEventSink? fallback = null,
        Func<CancellationToken, Task>? loadDatabaseLevelAsync = null, LoggingLevelSwitch? levelSwitch = null,
        int capacity = 10_000)
    {
        this.inner = inner;
        this.gate = gate;
        this.fallback = fallback;
        this.loadDatabaseLevelAsync = loadDatabaseLevelAsync;
        this.levelSwitch = levelSwitch;
        this.capacity = capacity;
        levelLoaded = loadDatabaseLevelAsync is null;

        // Emit() also checks gate.State (and levelLoaded) on every call and flushes there if it finds
        // both already true, but a quiet host - nothing else logged between Ready and here - would
        // otherwise leave the buffer sitting forever waiting for an Emit call that never comes. This
        // is what makes "flushes the moment the gate turns Ready" true regardless of what else is
        // happening.
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

        if (loadDatabaseLevelAsync is not null)
        {
            try
            {
                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(disposed.Token);
                timeoutSource.CancelAfter(LoadTimeout);
                await loadDatabaseLevelAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (disposed.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Best effort, covering both a genuine failure and LoadTimeout above: the flush
                // proceeds with whatever level the switch already carries - the compiled-in default,
                // unchanged, if nothing has loaded yet - rather than buffer forever. The signal this
                // waits on is normally set within milliseconds of the gate turning Ready (the same
                // reachable database that just satisfied the gate), so the timeout is a safety net
                // for the rare case the loader itself is still retrying, not the common path.
            }
        }

        lock (gateLock)
        {
            levelLoaded = true;
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
                if (gate.State == DatabaseState.Ready && levelLoaded)
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
        {
            var buffered = buffer.Dequeue();
            if (levelSwitch is null || buffered.Level >= levelSwitch.MinimumLevel)
                inner.Emit(buffered);
        }

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
