using Noof.Ledger.Application.Diagnostics.Integrity;

namespace Noof.Ledger.Application.Diagnostics.BugReports;

public interface IBugReportStore
{
    // Idempotent per (Source, ReplyTo) when ReplyTo is set: a redelivered report writes nothing and returns the
    // existing number, so a source writes the same canonical ReplyTo for the same message. One filed already
    // explained has its record summary and log lines taken now, best-effort.
    Task<BugReportSaved> FileAsync(NewBugReport report, CancellationToken cancellationToken);

    // The oldest Open, Pending report whose next attempt is due. No lease: one host runs one worker.
    Task<BugReportToExplain?> NextDueAsync(DateTimeOffset now, CancellationToken cancellationToken);

    // Taken and stored once, each part best-effort; a second call returns the stored snapshot.
    Task<BugReportSnapshot> TakeSnapshotAsync(Guid reportId, CancellationToken cancellationToken);

    Task CompleteExplanationAsync(Guid reportId, Explanation explanation, CancellationToken cancellationToken);

    // Returns the state after the attempt: Failed once attempts reach maxAttempts, else still Pending.
    Task<BugExplanationState> RecordFailedAttemptAsync(
        Guid reportId, DateTimeOffset nextAttemptAt, int maxAttempts, CancellationToken cancellationToken);

    // Open reports with a ReplyTo, explained (Done or Failed) and not yet delivered, by number.
    Task<IReadOnlyList<BugReportDelivery>> PendingDeliveriesAsync(CancellationToken cancellationToken);

    // deliveredAs is the source's own reference to the reply it sent, opaque like ReplyTo.
    Task MarkDeliveredAsync(Guid reportId, string deliveredAs, CancellationToken cancellationToken);

    // Newest number first.
    Task<IReadOnlyList<BugReportListItem>> ListAsync(bool includeClosed, CancellationToken cancellationToken);

    Task<BugReportDocument?> GetAsync(int number, CancellationToken cancellationToken);

    // Oldest number first.
    Task<IReadOnlyList<BugReportDocument>> LoadForExportAsync(bool includeClosed, CancellationToken cancellationToken);

    // False when the number is unknown or the report is already in that status.
    Task<bool> SetStatusAsync(int number, BugReportStatus status, CancellationToken cancellationToken);

    Task<int> CountOpenAsync(CancellationToken cancellationToken);
}
