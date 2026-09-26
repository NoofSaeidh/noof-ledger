using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Diagnostics;

internal sealed class LogSinkStatus : ILogSinkStatus
{
    readonly Lock gate = new();
    DateTimeOffset? lastFailureAt;

    public DateTimeOffset? LastFailureAt
    {
        get { lock (gate) return lastFailureAt; }
    }

    public void RecordFailure(DateTimeOffset at)
    {
        lock (gate) lastFailureAt = at;
    }
}
