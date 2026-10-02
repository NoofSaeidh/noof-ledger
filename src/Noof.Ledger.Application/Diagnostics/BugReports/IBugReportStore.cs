using Noof.Ledger.Application.Diagnostics.Integrity;

namespace Noof.Ledger.Application.Diagnostics.BugReports;

public interface IBugReportStore
{
    // Idempotent per (ChatId, MessageId): a redelivered /bug writes nothing and returns the existing number.
    Task<BugReportSaved> SaveFromTelegramAsync(TelegramBugReport report, CancellationToken cancellationToken);

    // Filed already explained; the record summary and log lines are taken now, best-effort.
    Task<int> CreateFromDashboardAsync(IntegrityFinding finding, Explanation explanation, CancellationToken cancellationToken);

    // The oldest Open, Pending report whose next attempt is due. No lease: one host runs one worker.
    Task<BugReportToExplain?> NextDueAsync(DateTimeOffset now, CancellationToken cancellationToken);

    // Taken and stored once, each part best-effort; a second call returns the stored snapshot.
    Task<BugReportSnapshot> TakeSnapshotAsync(Guid reportId, CancellationToken cancellationToken);

    Task CompleteExplanationAsync(Guid reportId, Explanation explanation, CancellationToken cancellationToken);

    // Returns the state after the attempt: Failed once attempts reach maxAttempts, else still Pending.
    Task<BugExplanationState> RecordFailedAttemptAsync(
        Guid reportId, DateTimeOffset nextAttemptAt, int maxAttempts, CancellationToken cancellationToken);

    Task<IReadOnlyList<BugReportDelivery>> PendingDeliveriesAsync(CancellationToken cancellationToken);

    Task MarkDeliveredAsync(Guid reportId, int replyMessageId, CancellationToken cancellationToken);

    // Newest number first.
    Task<IReadOnlyList<BugReportListItem>> ListAsync(bool includeClosed, CancellationToken cancellationToken);

    Task<BugReportDocument?> GetAsync(int number, CancellationToken cancellationToken);

    // Oldest number first.
    Task<IReadOnlyList<BugReportDocument>> LoadForExportAsync(bool includeClosed, CancellationToken cancellationToken);

    // False when the number is unknown or the report is already in that status.
    Task<bool> SetStatusAsync(int number, BugReportStatus status, CancellationToken cancellationToken);

    Task<int> CountOpenAsync(CancellationToken cancellationToken);
}
