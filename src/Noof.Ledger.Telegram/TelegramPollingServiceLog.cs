using Microsoft.Extensions.Logging;

namespace Noof.Ledger.Telegram;

// A sibling top-level static class, not nested inside TelegramPollingService (CLAUDE.md/contract:
// "[LoggerMessage] everywhere...a private static partial class Log inside each type or a sibling
// file"). Noof.Ledger.Telegram targets plain Microsoft.NET.Sdk, not Microsoft.NET.Sdk.Web; a
// [LoggerMessage] extension method nested inside a non-static class fails here with CS1109
// ("Extension methods must be defined in a top level static class") even though the identical
// pattern compiles cleanly for BackupWorker/CategorizationWorker/TranscriptionWorker in
// Noof.Ledger.Host. The difference tracks the SDK, not anything in this file; the sibling-file form
// sidesteps it without changing any call site or the EventIds/messages below.
internal static partial class TelegramPollingServiceLog
{
    [LoggerMessage(EventId = 1001, Level = LogLevel.Error,
        Message = "Telegram update {UpdateId} failed on attempt {Attempt}/{MaxAttempts}")]
    public static partial void UpdateFailed(this ILogger logger, Exception exception, int updateId, int attempt, int maxAttempts);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Error,
        Message = "Telegram update {UpdateId} failed {MaxAttempts} times; skipping it so later updates aren't blocked behind it")]
    public static partial void UpdateAbandoned(this ILogger logger, int updateId, int maxAttempts);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Error, Message = "Telegram poll tick failed; backing off and retrying")]
    public static partial void PollTickFailed(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Error,
        Message = "Failed to notify the operator that Telegram update {UpdateId} was skipped")]
    public static partial void SkippedUpdateNotificationFailed(this ILogger logger, Exception exception, int updateId);
}
