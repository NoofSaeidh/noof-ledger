using Serilog.Core;
using Serilog.Events;

namespace Noof.Ledger.Host.Logging;

// A no-op sink whose only job is to ride Serilog's own dispose chain (RedactingSink -> the Logger
// built from `destinations` -> every IDisposable sink it holds) so this host's SelfLogOwnership
// claim releases the moment the host's logging pipeline tears down, rather than being left for a
// later host to inherit.
internal sealed class SelfLogRegistrationSink(IDisposable registration) : ILogEventSink, IDisposable
{
    public void Emit(LogEvent logEvent) { }

    public void Dispose() => registration.Dispose();
}
