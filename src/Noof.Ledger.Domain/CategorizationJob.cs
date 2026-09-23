namespace Noof.Ledger.Domain;

public sealed class CategorizationJob
{
    public required Guid Id { get; init; }
    public required Guid TransactionId { get; init; }

    public JobKind Kind { get; init; }

    // The person's own words; set for a Correct job only, which a check constraint holds.
    public string? Instruction { get; init; }

    // The reply that asked for this correction. A unique index makes Telegram redelivering it a no-op.
    public int? SourceMessageId { get; init; }

    public required JobStatus Status { get; set; }
    public required int AttemptCount { get; set; }
    public required DateTimeOffset RunAfter { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
    public string? ClaimedBy { get; set; }
    public string? LastError { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; set; }
}
