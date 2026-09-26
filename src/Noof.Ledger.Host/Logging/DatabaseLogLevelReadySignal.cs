namespace Noof.Ledger.Host.Logging;

// Deliberately has zero dependencies of its own - not even ILogger<T> - because it exists to be
// resolved safely from inside LoggingSetup.Configure, which itself runs as part of building the
// ILoggerFactory singleton (Serilog.AspNetCore's UseSerilog reconfigure callback). Resolving
// anything there whose own construction needs ILogger<T> (DatabaseLogLevel included) re-enters
// ILoggerFactory's own not-yet-cached construction on the same thread and recurses until the
// process runs out of stack - confirmed against this DI container by a hung WebApplicationFactory
// test whose dump showed exactly that recursion, not a lock. A plain TaskCompletionSource holder
// carries the "the stored level has been applied" signal across that boundary with nothing to
// resolve reentrantly.
internal sealed class DatabaseLogLevelReadySignal
{
    readonly TaskCompletionSource loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void MarkLoaded() => loaded.TrySetResult();

    public Task WaitForLoadAsync(CancellationToken cancellationToken) => loaded.Task.WaitAsync(cancellationToken);
}
