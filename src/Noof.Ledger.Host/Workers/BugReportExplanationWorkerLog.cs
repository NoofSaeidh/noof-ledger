namespace Noof.Ledger.Host.Workers.BugReportLogging;

// A sibling top-level static class in its own namespace, for RecordExchangeWorkerLog's reason: TickFailed and friends
// must never become two visible extension-method candidates at one call site (CS0121). Numbers, attempts and failure
// kinds only — never a report's text, a record's text or the model's answer.
internal static partial class BugReportExplanationWorkerLog
{
    [LoggerMessage(EventId = 1901, Level = LogLevel.Error, Message = "Bug report worker tick failed")]
    public static partial void TickFailed(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1902, Level = LogLevel.Information,
        Message = "Bug report #{Number} explained (looks like a bug: {LooksLikeBug})")]
    public static partial void Explained(this ILogger logger, int number, bool looksLikeBug);

    [LoggerMessage(EventId = 1903, Level = LogLevel.Warning,
        Message = "Bug report #{Number}: explanation attempt {Attempt}/{MaxAttempts} failed ({FailureType})")]
    public static partial void ExplanationAttemptFailed(this ILogger logger, int number, int attempt, int maxAttempts, string failureType);

    [LoggerMessage(EventId = 1904, Level = LogLevel.Warning,
        Message = "Bug report #{Number}: no explanation after {MaxAttempts} attempts")]
    public static partial void ExplanationGaveUp(this ILogger logger, int number, int maxAttempts);

    [LoggerMessage(EventId = 1908, Level = LogLevel.Warning, Message = "Bug report #{Number}: snapshot taken with parts missing")]
    public static partial void SnapshotIncomplete(this ILogger logger, int number);

    [LoggerMessage(EventId = 1909, Level = LogLevel.Warning,
        Message = "Bug report #{Number}: the model refused the account; no attempt spent, explanations paused for {Cooldown}")]
    public static partial void AccountRefused(this ILogger logger, int number, TimeSpan cooldown);
}
