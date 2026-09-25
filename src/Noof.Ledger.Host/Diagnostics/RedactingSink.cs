using Serilog.Core;
using Serilog.Events;

namespace Noof.Ledger.Host.Diagnostics;

internal sealed class RedactingSink(ILogEventSink inner, SecretRedactor redactor) : ILogEventSink
{
    public void Emit(LogEvent logEvent) => inner.Emit(redactor.Redact(logEvent));
}
