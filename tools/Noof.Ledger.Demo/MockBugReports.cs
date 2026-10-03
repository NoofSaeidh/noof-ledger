using System.Globalization;
using System.Text.Json;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Diagnostics.BugReports;

namespace Noof.Ledger.Demo;

// Three reports as the app would have filed and answered them, already delivered or made on the dashboard, so the demo
// host's worker has nothing to explain or send and the demo never calls the model. Their JSON is the shape the store
// reads back.
internal static class MockBugReports
{
    // tracedAt is the traced record's occurred_at: its revisions and log lines carry the offsets MockDataWriter writes
    // them at, so report #2's record as filed agrees with that record's own trace in every month.
    public static IReadOnlyList<MockBugReport> Build(DateTimeOffset waitingSince, Guid tracedWalletId, DateTimeOffset tracedAt) =>
    [
        new(
            Id: MockData.Id(921),
            CreatedAt: MockData.At(19, 20, 15),
            Source: BugReportSource.Telegram,
            Text: "didn't record the exchange — I got 11700 dinars",
            TransactionId: MockData.WaitingTransactionId,
            ReplyTo: TelegramReplyTo(9_001),
            DeliveredAs: "19001",
            Status: BugReportStatus.Open,
            ClosedAt: null,
            SnapshotAt: MockData.At(19, 20, 16),
            RecordSummary: Lines(
                "Record: Expense · Failed · failure reason MissingReceivedAmount · captured from Text",
                $"Date: {MockData.Waiting.Day:yyyy-MM-dd}",
                "Wallet: (none)",
                "Text: exchanged 100 eur",
                "Lines: (none)",
                "Revisions: (none)"),
            FindingsJson: Json(Finding(
                "NotApplied", "WaitingOnYou", MockData.WaitingTransactionId, walletId: null,
                Text("Waiting for", "A reply to the echo"),
                Text("Status", "Failed"),
                Text("Reason", "MissingReceivedAmount"),
                Since("Idle for", waitingSince),
                Date("Date", MockData.Waiting.Day))),
            LogLinesJson: Json(
                Log(MockData.Waiting.OccurredAt.AddSeconds(3), LogSeverity.Information, "Noof.Ledger.Telegram.TelegramChatNotifier",
                    "Replied", properties: new() { ["Stage"] = "Replied" }),
                Log(MockData.Waiting.OccurredAt.AddSeconds(2), LogSeverity.Warning, "Noof.Ledger.Host.Workers.CategorizationWorker",
                    "The exchange names no received amount; the operator was asked for it"),
                Log(MockData.Waiting.OccurredAt, LogSeverity.Information, "Noof.Ledger.Telegram.TelegramUpdateRouter",
                    "Received", properties: new() { ["Stage"] = "Received" })),
            CollectionFailures: null,
            ExplanationState: BugExplanationState.Done,
            ExplanationAttempts: 0,
            Explanation: "The exchange of 100 EUR is saved but not recorded: the bot asked how many dinars you received, "
                + "and no reply came. Reply to the bot's message about it with the amount, for example \"11700 rsd\", and it "
                + "will be recorded. This is your data waiting, not a bug in the app.",
            LooksLikeBug: false),
        new(
            Id: MockData.Id(922),
            CreatedAt: MockData.At(20, 9, 30),
            Source: BugReportSource.Dashboard,
            Text: null,
            TransactionId: MockData.TracedTransactionId,
            ReplyTo: null,
            DeliveredAs: null,
            Status: BugReportStatus.Open,
            ClosedAt: null,
            SnapshotAt: MockData.At(20, 9, 30),
            RecordSummary: Lines(
                "Record: Expense · Completed · failure reason InvalidAmount · captured from Text",
                $"Date: {MockData.At(18, 0, 0):yyyy-MM-dd}",
                "Wallet: Wise",
                "Text: Lidl groceries 27.80, wine 12.50 eur",
                "Lines:",
                "- Groceries · 27.80 EUR · Groceries",
                "- Wine · 12.50 EUR · Groceries",
                "Revisions:",
                $"- {Minute(tracedAt.AddSeconds(2))} UTC · Initial · Captured → Completed",
                $"- {Minute(tracedAt.AddMinutes(2))} UTC · Correction · wine was 12.50, not 15"),
            FindingsJson: Json(Finding(
                "FactsMismatchKind", "Bug", MockData.TracedTransactionId, tracedWalletId,
                Text("Condition", "Failure reason on a record that is not failed"),
                Text("Status", "Completed"),
                Text("Failure reason", "InvalidAmount"))),
            LogLinesJson: Json(
                Log(tracedAt.AddMilliseconds(1520), LogSeverity.Information, "Noof.Ledger.Host.Workers.CategorizationWorker",
                    "Persisted", properties: new() { ["Stage"] = "Persisted" }),
                Log(tracedAt.AddMilliseconds(1450), LogSeverity.Information, "Noof.Ledger.Host.Workers.CategorizationWorker",
                    "Categorized", properties: new() { ["Stage"] = "Categorized" }),
                Log(tracedAt, LogSeverity.Information, "Noof.Ledger.Telegram.TelegramUpdateRouter",
                    "Received", properties: new() { ["Stage"] = "Received" })),
            CollectionFailures: null,
            ExplanationState: BugExplanationState.Done,
            ExplanationAttempts: 0,
            Explanation: "The record is completed but still carries the failure reason InvalidAmount, which only a failed "
                + "record may hold. Its lines and entries agree, so the balance is right; the stale reason is something the "
                + "app should have cleared when it recorded the lines. This looks like a bug in the app — file it.",
            LooksLikeBug: true),
        new(
            Id: MockData.Id(923),
            CreatedAt: MockData.At(20, 11, 5),
            Source: BugReportSource.Telegram,
            Text: "the dashboard total looks off",
            TransactionId: null,
            ReplyTo: TelegramReplyTo(9_003),
            DeliveredAs: "19003",
            Status: BugReportStatus.Closed,
            ClosedAt: MockData.At(20, 12, 0),
            SnapshotAt: MockData.At(20, 11, 6),
            RecordSummary: null,
            FindingsJson: null,
            LogLinesJson: Json(
                Log(MockData.At(20, 10, 40), LogSeverity.Error, "Noof.Ledger.Ai", "Categorisation call failed",
                    exception: "System.TimeoutException: The operation timed out after 00:00:30."),
                Log(MockData.At(20, 10, 12), LogSeverity.Warning, "Noof.Ledger.Ai", "Model call took 31.2 s, over its 30 s threshold")),
            CollectionFailures: "findings: check failed (TimeoutException)",
            ExplanationState: BugExplanationState.Failed,
            ExplanationAttempts: 3,
            Explanation: null,
            LooksLikeBug: null),
    ];

    // The Telegram layer's own reply address, "<chat id>:<message id>" - the form the BugReportsReplyTo migration
    // back-fills (R-2); delivered_as is the reply message id as text.
    static string TelegramReplyTo(int messageId) =>
        string.Create(CultureInfo.InvariantCulture, $"{MockData.TelegramChatId}:{messageId}");

    // \n whatever this file's own line endings are: the record summary is \n-joined, as RecordSummaryComposer writes it.
    static string Lines(params string[] lines) => string.Join('\n', lines);

    static string Json(params Dictionary<string, object?>[] items) => JsonSerializer.Serialize(items);

    static Dictionary<string, object?> Finding(
        string check, string group, Guid transactionId, Guid? walletId, params Dictionary<string, object?>[] facts) => new()
    {
        ["check"] = check,
        ["group"] = group,
        ["transaction_id"] = transactionId,
        ["wallet_id"] = walletId,
        ["job_id"] = null,
        ["facts"] = facts,
    };

    static Dictionary<string, object?> Text(string name, string text) =>
        new() { ["kind"] = "text", ["name"] = name, ["text"] = text };

    static Dictionary<string, object?> Since(string name, DateTimeOffset since) =>
        new() { ["kind"] = "since", ["name"] = name, ["since"] = Instant(since) };

    static Dictionary<string, object?> Date(string name, DateOnly day) =>
        new() { ["kind"] = "date", ["name"] = name, ["day"] = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };

    static Dictionary<string, object?> Log(
        DateTimeOffset at, LogSeverity level, string source, string message, string? exception = null,
        Dictionary<string, string>? properties = null) => new()
    {
        ["logged_at"] = Instant(at),
        ["level"] = level.ToString(),
        ["source"] = source,
        ["message"] = message,
        ["exception"] = exception,
        ["properties"] = properties,
    };

    static string Minute(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    static string Instant(DateTimeOffset at) => at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}

internal sealed record MockBugReport(
    Guid Id,
    DateTimeOffset CreatedAt,
    BugReportSource Source,
    string? Text,
    Guid? TransactionId,
    string? ReplyTo,
    string? DeliveredAs,
    BugReportStatus Status,
    DateTimeOffset? ClosedAt,
    DateTimeOffset SnapshotAt,
    string? RecordSummary,
    string? FindingsJson,
    string LogLinesJson,
    string? CollectionFailures,
    BugExplanationState ExplanationState,
    int ExplanationAttempts,
    string? Explanation,
    bool? LooksLikeBug);
