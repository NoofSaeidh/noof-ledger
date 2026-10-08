using Noof.Ledger.Application.Diagnostics.BugReports;

namespace Noof.Ledger.Persistence.BugReports;

// A report is evidence, like a revision: its snapshot columns keep what the ledger looked like when it was filed.
internal sealed class BugReport
{
    public required Guid Id { get; init; }

    // GENERATED ALWAYS AS IDENTITY: the database numbers reports, and refuses a number anyone else supplies.
    public int Number { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
    public required BugReportSource Source { get; init; }
    public string? Text { get; init; }
    public Guid? TransactionId { get; init; }
    public string? ReplyTo { get; init; }
    public required BugReportStatus Status { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset? SnapshotAt { get; set; }
    public string? RecordSummary { get; set; }
    public string? FindingsJson { get; set; }
    public string? LogLinesJson { get; set; }
    public string? CollectionFailures { get; set; }
    public required BugExplanationState ExplanationState { get; set; }
    public required int ExplanationAttempts { get; set; }
    public required DateTimeOffset ExplanationNextAt { get; set; }
    public string? Explanation { get; set; }
    public bool? LooksLikeBug { get; set; }
    public string? DeliveredAs { get; set; }
}
