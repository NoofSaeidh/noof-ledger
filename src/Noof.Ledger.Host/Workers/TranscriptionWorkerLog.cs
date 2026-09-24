namespace Noof.Ledger.Host.Workers.TranscriptionLogging;

// A sibling top-level static class, not nested inside TranscriptionWorker: a [LoggerMessage]
// extension method nested inside a non-static class fails to compile here with CS1109 ("Extension
// methods must be defined in a top level static class"), verified by a clean rebuild, not by
// documentation. In its own namespace (not plain Noof.Ledger.Host.Workers) so its
// JobAlreadyReclaimed and AccountLevelFailure - names CategorizationWorkerLog also uses with the
// same signature - do not become simultaneously visible extension-method candidates and turn every
// call site ambiguous (CS0121); TranscriptionWorker.cs imports only this namespace.
internal static partial class TranscriptionWorkerLog
{
    [LoggerMessage(EventId = 1301, Level = LogLevel.Error, Message = "Transcription worker tick failed")]
    public static partial void TickFailed(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1302, Level = LogLevel.Information,
        Message = "Job {JobId}'s transcript was already handed on by an earlier run")]
    public static partial void TranscriptAlreadyHandedOn(this ILogger logger, Guid jobId);

    [LoggerMessage(EventId = 1303, Level = LogLevel.Warning,
        Message = "Account-level speech provider failure on job {JobId} ({Message}); pausing new claims for {Cooldown}")]
    public static partial void AccountLevelFailure(this ILogger logger, Guid jobId, string message, TimeSpan cooldown);

    [LoggerMessage(EventId = 1304, Level = LogLevel.Warning,
        Message = "Failed to edit Telegram message {MessageId} for transaction {TransactionId}")]
    public static partial void EditFailed(this ILogger logger, Exception exception, int messageId, Guid transactionId);

    [LoggerMessage(EventId = 1305, Level = LogLevel.Warning,
        Message = "Job {JobId} was already reclaimed by another worker; not retrying")]
    public static partial void JobAlreadyReclaimed(this ILogger logger, Guid jobId);

    [LoggerMessage(EventId = 1306, Level = LogLevel.Warning,
        Message = "SucceedAsync failed for job {JobId} after its transcript was already handed on")]
    public static partial void SucceedAfterHandOffFailed(this ILogger logger, Exception exception, Guid jobId);
}
