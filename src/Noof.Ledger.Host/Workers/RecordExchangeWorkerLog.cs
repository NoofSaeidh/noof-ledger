using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Host.Workers.RecordExchangeLogging;

// A sibling top-level static class in its own namespace, for TranscriptionWorkerLog's reason: the method names
// every worker log shares (TickFailed, JobAlreadyReclaimed, LogStageFailed, ...) must never become simultaneously
// visible extension-method candidates at one call site (CS0121).
internal static partial class RecordExchangeWorkerLog
{
    [LoggerMessage(EventId = 1801, Level = LogLevel.Error, Message = "Record-exchange worker tick failed")]
    public static partial void TickFailed(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1802, Level = LogLevel.Warning,
        Message = "Job {JobId} was already reclaimed by another worker; not retrying")]
    public static partial void JobAlreadyReclaimed(this ILogger logger, Guid jobId);

    [LoggerMessage(EventId = 1803, Level = LogLevel.Warning,
        Message = "SucceedAsync failed for job {JobId} after its exchange was already recorded; the transaction is left as recorded")]
    public static partial void SucceedAfterCommitFailed(this ILogger logger, Exception exception, Guid jobId);

    [LoggerMessage(EventId = 1804, Level = LogLevel.Warning,
        Message = "Failed to echo job {JobId}'s result to Telegram; the exchange itself was already handled")]
    public static partial void EchoFailed(this ILogger logger, Exception exception, Guid jobId);

    [LoggerMessage(EventId = 1805, Level = LogLevel.Warning,
        Message = "The slip of transaction {TransactionId} was not recorded: {Reason}")]
    public static partial void SlipNotRecorded(this ILogger logger, Guid transactionId, RecordFailureReason reason);

    [LoggerMessage(EventId = 1806, Level = LogLevel.Information,
        Message = "Transaction {TransactionId} was already recorded ({Status}); its slip is not applied over it")]
    public static partial void SlipAlreadyRecorded(this ILogger logger, Guid transactionId, TransactionStatus status);

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
