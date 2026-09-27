using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Host.Workers.ExtractReceiptLogging;

// A sibling top-level static class, its own namespace - same reasoning as TranscriptionWorkerLog:
// CS1109 for a nested [LoggerMessage] class, and a separate namespace so this class's own
// JobAlreadyReclaimed/SucceedAfterHandOffFailed/EditFailed/AccountLevelFailure (same names, same
// signatures as TranscriptionWorkerLog's) never become simultaneously visible extension-method
// candidates at a shared call site.
internal static partial class ExtractReceiptWorkerLog
{
    [LoggerMessage(EventId = 1701, Level = LogLevel.Error, Message = "Extract-receipt worker tick failed")]
    public static partial void TickFailed(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1702, Level = LogLevel.Warning,
        Message = "Job {JobId} was already reclaimed by another worker; not retrying")]
    public static partial void JobAlreadyReclaimed(this ILogger logger, Guid jobId);

    [LoggerMessage(EventId = 1703, Level = LogLevel.Warning,
        Message = "SucceedAsync failed for job {JobId} after its outcome was already handed on")]
    public static partial void SucceedAfterHandOffFailed(this ILogger logger, Exception exception, Guid jobId);

    [LoggerMessage(EventId = 1704, Level = LogLevel.Warning,
        Message = "Failed to edit Telegram message {MessageId} for transaction {TransactionId}")]
    public static partial void EditFailed(this ILogger logger, Exception exception, int messageId, Guid transactionId);

    [LoggerMessage(EventId = 1705, Level = LogLevel.Warning,
        Message = "Account-level model failure on job {JobId} ({Message}); pausing new claims for {Cooldown}")]
    public static partial void AccountLevelFailure(this ILogger logger, Guid jobId, string message, TimeSpan cooldown);

    [LoggerMessage(EventId = 1706, Level = LogLevel.Information,
        Message = "Transaction {TransactionId} already has a receipt; not re-extracting a replayed job")]
    public static partial void LogReceiptAlreadyExtracted(this ILogger logger, Guid transactionId);

    [LoggerMessage(EventId = 5011, Level = LogLevel.Debug, Message = "QR decoded: total {Total}, issued {IssuedAt}")]
    public static partial void LogQrDecoded(this ILogger logger, decimal total, DateTimeOffset issuedAt);

    [LoggerMessage(EventId = 5012, Level = LogLevel.Information, Message = "Receipt fetched: {Lines} lines, total {Total}")]
    public static partial void LogReceiptFetched(this ILogger logger, int lines, decimal total);

    [LoggerMessage(EventId = TransactionStages.ReceiptFetchFailedEventId, EventName = TransactionStages.ReceiptFetchFailed,
        Level = LogLevel.Warning, Message = "{Stage}: {Reason} (status {StatusCode})")]
    public static partial void LogReceiptFetchFailed(this ILogger logger, string stage, string reason, int? statusCode);

    [LoggerMessage(EventId = 5013, Level = LogLevel.Information, Message = "Vision used: {Reason}")]
    public static partial void LogVisionUsed(this ILogger logger, string reason);

    [LoggerMessage(EventId = 5014, Level = LogLevel.Information, Message = "Receipt duplicate of transaction {DuplicateTransactionId}")]
    public static partial void LogReceiptDuplicate(this ILogger logger, Guid duplicateTransactionId);

    [LoggerMessage(EventId = TransactionStages.ExtractedEventId, EventName = TransactionStages.Extracted, Level = LogLevel.Information,
        Message = "{Stage}: {Source}, {Lines} lines, total {Total}, qrTotal {QrTotal}, mismatch {Mismatch}, fetchFailed {FetchFailed}")]
    public static partial void LogExtracted(
        this ILogger logger, string stage, ReceiptSource source, int lines, decimal total,
        decimal? qrTotal, bool mismatch, bool fetchFailed);

    [LoggerMessage(EventId = TransactionStages.StageFailedEventId, EventName = TransactionStages.StageFailed, Level = LogLevel.Error,
        Message = "{Stage} at stage {FailedStage}")]
    public static partial void LogStageFailed(this ILogger logger, string stage, string failedStage, Exception exception);

    [LoggerMessage(EventId = 5015, Level = LogLevel.Warning,
        Message = "Transaction {TransactionId} saved without categorising: mismatch {Mismatch}, malformed tax id {TaxIdMalformed}")]
    public static partial void LogAwaitingConfirmation(this ILogger logger, Guid transactionId, bool mismatch, bool taxIdMalformed);

    [LoggerMessage(EventId = 5016, Level = LogLevel.Debug,
        Message = "Vision total {ModelTotal} discarded in favour of the verified QR total {QrTotal}")]
    public static partial void LogModelTotalDiscardedForQrTotal(this ILogger logger, decimal modelTotal, decimal qrTotal);

    [LoggerMessage(EventId = 5017, Level = LogLevel.Debug,
        Message = "Vision currency {ModelCurrency} discarded in favour of the verified QR currency RSD")]
    public static partial void LogModelCurrencyDiscardedForQrCurrency(this ILogger logger, CurrencyCode modelCurrency);

    [LoggerMessage(EventId = 5018, Level = LogLevel.Debug,
        Message = "Vision issued-at {ModelIssuedAt} discarded in favour of the verified QR issued-at {QrIssuedAt}")]
    public static partial void LogModelIssuedAtDiscardedForQrIssuedAt(
        this ILogger logger, DateTimeOffset? modelIssuedAt, DateTimeOffset qrIssuedAt);

    [LoggerMessage(EventId = 5019, Level = LogLevel.Debug,
        Message = "Vision kind {ModelKind} discarded in favour of the verified QR kind {QrKind}")]
    public static partial void LogModelKindDiscardedForQrKind(this ILogger logger, ReceiptKind modelKind, ReceiptKind qrKind);

    [LoggerMessage(EventId = 5020, Level = LogLevel.Debug,
        Message = "Vision fiscal number {ModelFiscalNumber} discarded in favour of the verified QR fiscal number {QrFiscalNumber}")]
    public static partial void LogModelFiscalNumberDiscardedForQrFiscalNumber(
        this ILogger logger, string? modelFiscalNumber, string qrFiscalNumber);
}
