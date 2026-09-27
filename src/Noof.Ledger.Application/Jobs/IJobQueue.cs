using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Jobs;

public interface IJobQueue
{
    // Claims one pending, due job of one of the given kinds for workerId and marks it Claimed. lease is how long the
    // claim is held before ReleaseExpiredLeasesAsync may release it back to Pending - there is no separate
    // lease-expiry column, so claiming sets RunAfter to now + lease. Returns null when nothing claimable is pending
    // and due. The kind filter never weakens the ordering rule: a job still waits for every earlier job of the same
    // transaction, whatever that job's kind.
    Task<CategorizationJob?> ClaimAsync(
        string workerId, IReadOnlyCollection<JobKind> kinds, TimeSpan lease, CancellationToken cancellationToken);

    // Claims one due CategorizeReceipt job whose receipt is a non-money kind
    // (ReceiptKindExtensions.IsNonMoneyKind), regardless of whether a model key is configured. A
    // Copy/Training/Proforma/Advance slip is recognised and cancelled without ever calling the model
    // (ReceiptCategorizationWorker's own short-circuit), so gating it behind IModelProvider like every
    // other CategorizeReceipt job left it stuck at "Categorising..." forever with no key configured.
    // Kept separate from ClaimAsync so every other job kind, and a money receipt, keep gating on the
    // model key exactly as before - this claim never burns an attempt on a job that would otherwise
    // wait indefinitely for one.
    Task<CategorizationJob?> ClaimNonMoneyReceiptAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken);

    // Every completion verb below only affects a job that is still Claimed by this exact workerId -
    // claimed_by and status both participate in the WHERE clause. A worker whose lease was released
    // and reclaimed by someone else gets JobCompletionOutcome.NotOwned instead of silently mutating
    // whatever the new owner has already done to the row; the caller must log that and stop, not retry.
    Task<JobCompletionOutcome> SucceedAsync(Guid jobId, string workerId, CancellationToken cancellationToken);

    // Returns the job to Pending at runAfter, unless the job is already at the attempt cap,
    // in which case it goes straight to Failed instead and runAfter is ignored.
    Task<JobCompletionOutcome> RetryAsync(Guid jobId, string workerId, DateTimeOffset runAfter, string error, CancellationToken cancellationToken);

    Task<JobCompletionOutcome> FailAsync(Guid jobId, string workerId, string error, CancellationToken cancellationToken);

    // Hands a claimed Correct/Reinterpret job to the receipt-correction path at claim time (ruling
    // F-2, Phase 6 re-review): the transaction it points at already has a money receipt, so its line
    // items must come from receipt_lines, never a fresh record_transaction reading. Queues a
    // CategorizeReceipt job carrying instruction as its own Instruction and marks jobId Succeeded,
    // both inside one database transaction - a crash before it commits leaves jobId Claimed for an
    // ordinary lease-expiry retry, never a hand-off job created without its origin marked done.
    // NotOwned (jobId no longer Claimed by workerId) creates nothing.
    Task<JobCompletionOutcome> HandOffToReceiptCorrectionAsync(
        Guid jobId, string workerId, Guid transactionId, string? instruction, int? sourceMessageId, DateTimeOffset now,
        CancellationToken cancellationToken);

    // Returns claimed jobs whose lease (RunAfter) has passed to Pending. Intended to run on
    // every poll tick, never as a startup sweep - a startup sweep would steal live work from
    // a second Host process.
    Task<int> ReleaseExpiredLeasesAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
