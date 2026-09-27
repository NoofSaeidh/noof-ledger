using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Host.Workers.ReceiptCategorizationLogging;

// A sibling top-level static class for CategorizationWorkerLog's own reason (CS1109 on a nested
// class), in its own namespace so ReceiptCategorizationWorker.cs's names never collide as
// simultaneously visible extension-method candidates with CategorizationWorkerLog's own.
internal static partial class ReceiptCategorizationWorkerLog
{
    [LoggerMessage(EventId = 1601, Level = LogLevel.Error, Message = "Receipt categorization worker tick failed")]
    public static partial void TickFailed(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1602, Level = LogLevel.Warning,
        Message = "Job {JobId} was already reclaimed by another worker; not retrying")]
    public static partial void JobAlreadyReclaimed(this ILogger logger, Guid jobId);

    [LoggerMessage(EventId = 1603, Level = LogLevel.Warning,
        Message = "SucceedAsync failed for job {JobId} after its line items were already committed; the transaction is left as it was written")]
    public static partial void SucceedAfterCommitFailed(this ILogger logger, Exception exception, Guid jobId);

    [LoggerMessage(EventId = 1604, Level = LogLevel.Warning,
        Message = "Account-level model provider failure on job {JobId} ({Message}); pausing new claims for {Cooldown}")]
    public static partial void AccountLevelFailure(this ILogger logger, Guid jobId, string message, TimeSpan cooldown);

    [LoggerMessage(EventId = 1605, Level = LogLevel.Warning,
        Message = "Failed to echo job {JobId}'s result to Telegram; the categorization itself already succeeded")]
    public static partial void EchoFailed(this ILogger logger, Exception exception, Guid jobId);

    [LoggerMessage(EventId = 1606, Level = LogLevel.Warning,
        Message = "Failed to edit Telegram message {MessageId} to report a failed job for transaction {TransactionId}")]
    public static partial void FailureEditFailed(this ILogger logger, Exception exception, int messageId, Guid transactionId);

    [LoggerMessage(EventId = 1609, Level = LogLevel.Warning,
        Message = "Failed to edit Telegram message {MessageId} with a retry notice for transaction {TransactionId}")]
    public static partial void RetryNoticeEditFailed(this ILogger logger, Exception exception, int messageId, Guid transactionId);

    [LoggerMessage(EventId = 1607, Level = LogLevel.Information,
        Message = "categorize_receipt did not answer ordinal {Ordinal}; falling back to \"{FallbackSlug}\"")]
    public static partial void MissingOrdinal(this ILogger logger, int ordinal, string fallbackSlug);

    [LoggerMessage(EventId = 1608, Level = LogLevel.Information,
        Message = "Receipt kind {Kind} for transaction {TransactionId} is not a purchase; nothing was recorded")]
    public static partial void ReceiptNotRecorded(this ILogger logger, string kind, Guid transactionId);

    [LoggerMessage(EventId = TransactionStages.CategorizedEventId, EventName = TransactionStages.Categorized, Level = LogLevel.Information,
        Message = "{Stage} as {Kind} for wallet {WalletId}: {Summary}")]
    public static partial void LogCategorized(this ILogger logger, string stage, TransactionKind kind, Guid? walletId, string summary);

    [LoggerMessage(EventId = TransactionStages.PersistedEventId, EventName = TransactionStages.Persisted, Level = LogLevel.Information,
        Message = "{Stage} as {Kind}")]
    public static partial void LogPersisted(this ILogger logger, string stage, TransactionKind kind);

    [LoggerMessage(EventId = TransactionStages.RepliedEventId, EventName = TransactionStages.Replied, Level = LogLevel.Information,
        Message = "{Stage} to bot message {BotMessageId}")]
    public static partial void LogReplied(this ILogger logger, string stage, int botMessageId);

    [LoggerMessage(EventId = TransactionStages.StageFailedEventId, EventName = TransactionStages.StageFailed, Level = LogLevel.Error,
        Message = "{Stage} at stage {FailedStage}")]
    public static partial void LogStageFailed(this ILogger logger, string stage, string failedStage, Exception exception);
}
