using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Diagnostics;

internal sealed class PollingHeartbeat : IPollingHeartbeat
{
    readonly Lock gate = new();
    DateTimeOffset? lastSuccessAt;
    (DateTimeOffset At, PollFailure Failure)? lastFailure;

    public DateTimeOffset? LastSuccessAt
    {
        get { lock (gate) return lastSuccessAt; }
    }

    public (DateTimeOffset At, PollFailure Failure)? LastFailure
    {
        get { lock (gate) return lastFailure; }
    }

    public void RecordSuccess(DateTimeOffset at)
    {
        lock (gate)
        {
            lastSuccessAt = at;
            lastFailure = null;
        }
    }

    public void RecordFailure(DateTimeOffset at, PollFailure failure)
    {
        lock (gate)
            lastFailure = (at, failure);
    }
}
