using Serilog.Core;
using Serilog.Events;

namespace Noof.Ledger.Host.Diagnostics;

// Serilog's Logger.Dispose() only closes a sink it holds directly when that sink is itself
// IDisposable. RedactingSink sits between the outer pipeline and console+file+Postgres, so it
// forwards Dispose to `inner` (a full Logger built from `destinations` in LoggingSetup) - without
// this, the file sink's handle and the Postgres batch never close on host shutdown.
internal sealed class RedactingSink(ILogEventSink inner, SecretRedactor redactor) : ILogEventSink, IDisposable
{
    public void Emit(LogEvent logEvent) => inner.Emit(redactor.Redact(logEvent));

    public void Dispose()
    {
        if (inner is IDisposable disposable)
            disposable.Dispose();
    }
}
