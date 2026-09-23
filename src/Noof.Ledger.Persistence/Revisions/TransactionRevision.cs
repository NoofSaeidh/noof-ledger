using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Revisions;

internal sealed class TransactionRevision
{
    public required Guid Id { get; init; }
    public required Guid TransactionId { get; init; }
    public required int RevisionNumber { get; init; }
    public required RevisionKind Kind { get; init; }
    public string? Instruction { get; init; }

    // StatusBefore is what Restore restores: the status a record had before the cancellation it undoes.
    public required TransactionStatus StatusBefore { get; init; }
    public required TransactionStatus StatusAfter { get; init; }

    public required string Snapshot { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
