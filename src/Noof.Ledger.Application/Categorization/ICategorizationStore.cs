namespace Noof.Ledger.Application.Categorization;

public interface ICategorizationStore
{
    Task<CategorizationSubject?> GetSubjectAsync(Guid transactionId, CancellationToken cancellationToken);

    // Replaces model-authored line items for this transaction and sets status = Completed, in ONE
    // database transaction. A line whose categorized_by is above Model is never touched, so a
    // hand-corrected line survives a re-run. Idempotent: running it twice leaves the same rows.
    Task ApplyAsync(Guid transactionId, IReadOnlyList<CategorizedLineItem> items, CancellationToken cancellationToken);

    Task MarkFailedAsync(Guid transactionId, CancellationToken cancellationToken);
}
