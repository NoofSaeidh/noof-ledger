using Noof.Ledger.Application.Receipts;

namespace Noof.Ledger.Host.Diagnostics;

// Task 6 builds the Receipts health check on this; this task only records what ExtractReceiptWorker
// observes. Same shape as LogSinkStatus: a singleton, thread-safe via Lock, holding nothing that
// needs to survive a restart.
internal sealed class ReceiptFetchStatus : IReceiptFetchStatus
{
    readonly Lock gate = new();
    DateTimeOffset? lastFailureAt;
    string? lastFailureReason;

    public DateTimeOffset? LastFailureAt
    {
        get { lock (gate) return lastFailureAt; }
    }

    public string? LastFailureReason
    {
        get { lock (gate) return lastFailureReason; }
    }

    public void RecordFailure(DateTimeOffset at, string reason)
    {
        lock (gate)
        {
            lastFailureAt = at;
            lastFailureReason = reason;
        }
    }
}
