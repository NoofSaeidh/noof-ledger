namespace Noof.Ledger.Host.Workers.CategorizationLogging;

// A sibling top-level static class, not nested inside CategorizationWorker: a [LoggerMessage]
// extension method nested inside a non-static class fails to compile here with CS1109 ("Extension
// methods must be defined in a top level static class"), verified by a clean rebuild, not by
// documentation. In its own namespace (not plain Noof.Ledger.Host.Workers) so its JobAlreadyReclaimed
// and AccountLevelFailure - names TranscriptionWorkerLog also uses with the same signature - do not
// become simultaneously visible extension-method candidates and turn every call site ambiguous
// (CS0121); CategorizationWorker.cs imports only this namespace.
internal static partial class CategorizationWorkerLog
{
    [LoggerMessage(EventId = 1201, Level = LogLevel.Error, Message = "Categorization worker tick failed")]
    public static partial void TickFailed(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1202, Level = LogLevel.Information,
        Message = "Job {JobId} reached the canonicalization cap of {Cap}; '{MerchantText}' was left unlinked")]
    public static partial void CanonicalizationCapReached(this ILogger logger, Guid jobId, int cap, string merchantText);

    [LoggerMessage(EventId = 1203, Level = LogLevel.Warning,
        Message = "Job {JobId} was already reclaimed by another worker; not retrying")]
    public static partial void JobAlreadyReclaimed(this ILogger logger, Guid jobId);

    [LoggerMessage(EventId = 1204, Level = LogLevel.Warning,
        Message = "SucceedAsync failed for job {JobId} after its line items were already committed; the transaction is left as Completed")]
    public static partial void SucceedAfterCommitFailed(this ILogger logger, Exception exception, Guid jobId);

    [LoggerMessage(EventId = 1205, Level = LogLevel.Warning,
        Message = "Account-level model provider failure on job {JobId} ({Message}); pausing new claims for {Cooldown}")]
    public static partial void AccountLevelFailure(this ILogger logger, Guid jobId, string message, TimeSpan cooldown);

    [LoggerMessage(EventId = 1206, Level = LogLevel.Warning,
        Message = "Failed to echo job {JobId}'s result to Telegram; the categorization itself already succeeded")]
    public static partial void EchoFailed(this ILogger logger, Exception exception, Guid jobId);

    [LoggerMessage(EventId = 1207, Level = LogLevel.Warning,
        Message = "Failed to edit Telegram message {MessageId} to report a failed job for transaction {TransactionId}")]
    public static partial void FailureEditFailed(this ILogger logger, Exception exception, int messageId, Guid transactionId);
}
