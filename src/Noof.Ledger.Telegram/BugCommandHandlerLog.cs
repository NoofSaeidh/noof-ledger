using Microsoft.Extensions.Logging;

namespace Noof.Ledger.Telegram;

// A sibling top-level static class, for TelegramUpdateRouterLog's reason (CS1109 on a nested one). No log line names a
// chat, a message or the report's text: numbers and flags only.
internal static partial class BugCommandHandlerLog
{
    [LoggerMessage(EventId = 6201, Level = LogLevel.Debug, Message = "Rejected a /bug command from a non-owner chat")]
    public static partial void BugCommandRejected(this ILogger logger);

    [LoggerMessage(EventId = 6202, Level = LogLevel.Information,
        Message = "Bug report #{Number} saved from Telegram (linked to a record: {Linked}, new: {Created})")]
    public static partial void BugReportSavedFromTelegram(this ILogger logger, int number, bool linked, bool created);

    [LoggerMessage(EventId = 6203, Level = LogLevel.Debug, Message = "Ignored a bug report button from a non-owner chat")]
    public static partial void BugReportButtonRejected(this ILogger logger);

    [LoggerMessage(EventId = 6204, Level = LogLevel.Information, Message = "Bug report #{Number} closed from Telegram")]
    public static partial void BugReportClosedFromTelegram(this ILogger logger, int number);
}
