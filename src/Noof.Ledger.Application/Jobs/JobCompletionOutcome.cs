namespace Noof.Ledger.Application.Jobs;

// Returned by every completion verb (SucceedAsync, RetryAsync, FailAsync) so a caller can tell a
// real completion from a no-op. NotOwned means the WHERE clause matched zero rows - the job wasn't
// Claimed by this workerId any more, because someone else's lease-expiry claim already moved it on.
// That is not an error: it is the expected outcome for a worker whose lease was already released
// and reclaimed, and the caller is expected to log it and stop, not retry the call.
public enum JobCompletionOutcome
{
    Applied,
    NotOwned,
}
