using Microsoft.Extensions.Logging;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Telegram;

// A sibling top-level static class, not nested inside TelegramUpdateRouter: a [LoggerMessage]
// extension method nested inside a non-static class fails to compile here with CS1109 ("Extension
// methods must be defined in a top level static class"), verified in Task 2/3's workers.
internal static partial class TelegramUpdateRouterLog
{
    [LoggerMessage(EventId = TransactionStages.ReceivedEventId, EventName = TransactionStages.Received, Level = LogLevel.Information,
        Message = "{Stage} capture kind {CaptureKind} from chat {ChatId}")]
    public static partial void LogReceived(this ILogger logger, string stage, CaptureKind captureKind, long chatId);

    [LoggerMessage(EventId = TransactionStages.StageFailedEventId, EventName = TransactionStages.StageFailed, Level = LogLevel.Error,
        Message = "{Stage} at stage {FailedStage}")]
    public static partial void LogStageFailed(this ILogger logger, string stage, string failedStage, Exception exception);

    [LoggerMessage(EventId = 6001, Level = LogLevel.Debug, Message = "Rejected a /health command from a non-owner chat")]
    public static partial void LogHealthCommandRejected(this ILogger logger);
}
