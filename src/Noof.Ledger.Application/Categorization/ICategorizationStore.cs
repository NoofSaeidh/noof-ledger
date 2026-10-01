using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Categorization;

public interface ICategorizationStore
{
    Task<CategorizationSubject?> GetSubjectAsync(Guid transactionId, CancellationToken cancellationToken);

    // Replaces model-authored line items for this transaction and sets status = Completed and occurred_on,
    // in ONE database transaction. A line whose categorized_by is above Model is never touched, so a
    // hand-corrected line survives a re-run. Idempotent: running it twice leaves the same rows.
    Task ApplyAsync(Guid transactionId, CategorizationOutcome outcome, CancellationToken cancellationToken);

    // Both overloads fail only a record that is still Captured: a correction or a Cancel that got there first wins,
    // and the failure changes nothing.
    Task MarkFailedAsync(Guid transactionId, CancellationToken cancellationToken);

    // A first reading that cannot be recorded for a reason the echo names: status = Failed and failure_reason
    // in one statement.
    Task MarkFailedAsync(Guid transactionId, RecordFailureReason reason, CancellationToken cancellationToken);
}
