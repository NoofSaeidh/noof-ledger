namespace Noof.Ledger.Domain;

public sealed class CategorizationJob
{
    public required Guid Id { get; init; }
    public required Guid TransactionId { get; init; }
    public required JobStatus Status { get; set; }
    public required int AttemptCount { get; set; }
    public required DateTimeOffset RunAfter { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
    public string? ClaimedBy { get; set; }
    public string? LastError { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; set; }
}
