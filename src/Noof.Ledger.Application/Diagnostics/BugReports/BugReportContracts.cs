using Noof.Ledger.Application.Diagnostics.Integrity;

namespace Noof.Ledger.Application.Diagnostics.BugReports;

public enum BugReportSource { Telegram = 0, Dashboard = 1 }

public enum BugReportStatus { Open = 0, Closed = 1 }

public enum BugExplanationState { Pending = 0, Done = 1, Failed = 2 }

public sealed record TelegramBugReport(long ChatId, int MessageId, string? Text, Guid? TransactionId);

// Created is false when a redelivered /bug found the report it had already filed.
public sealed record BugReportSaved(int Number, bool Created);

// Null Findings were not collected (CollectionFailures names why) - never an empty list standing in for them.
public sealed record BugReportSnapshot(
    DateTimeOffset TakenAt, string? RecordSummary, IReadOnlyList<IntegrityFinding>? Findings, string? CollectionFailures);

public sealed record BugReportToExplain(
    Guid Id, int Number, int Attempts, string? Text, Guid? TransactionId, BugReportSnapshot? Snapshot);

// A null FindingsCount means the findings were not collected.
public sealed record BugReportDelivery(
    Guid Id, int Number, long ChatId, int MessageId, BugExplanationState State, string? Explanation, bool? LooksLikeBug,
    bool Linked, int? FindingsCount);

public sealed record BugReportListItem(
    int Number, DateTimeOffset CreatedAt, BugReportSource Source, BugReportStatus Status, string? Text, Guid? TransactionId,
    BugExplanationState ExplanationState);

public sealed record BugReportLogLine(
    DateTimeOffset LoggedAt, LogSeverity Level, string? Source, string Message, string? Exception, string? PropertiesJson);

// Everything the Markdown and /bugs/{number} show. It carries no Telegram id, so neither can print one, and its free
// text is already URL-stripped. Revisions are read live ([] when unlinked); a null list was not collected - or, for
// FindingsNow, the report is unlinked or the live check failed.
public sealed record BugReportDocument(
    int Number, DateTimeOffset CreatedAt, BugReportSource Source, BugReportStatus Status, DateTimeOffset? ClosedAt,
    string? Text, Guid? TransactionId, DateTimeOffset? SnapshotAt, string? RecordSummary,
    IReadOnlyList<RevisionView> Revisions,
    IReadOnlyList<IntegrityFinding>? FindingsThen,
    IReadOnlyList<IntegrityFinding>? FindingsNow,
    string? CollectionFailures,
    BugExplanationState ExplanationState, string? Explanation, bool? LooksLikeBug,
    IReadOnlyList<BugReportLogLine>? LogLines);
