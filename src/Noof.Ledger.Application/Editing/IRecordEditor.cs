namespace Noof.Ledger.Application.Editing;

// EchoMessageId is null only when the acknowledgement could not be sent at capture time.
public sealed record EchoTarget(Guid TransactionId, int? EchoMessageId);

public interface IRecordEditor
{
    Task<EchoTarget?> FindByBotMessageAsync(long chatId, int messageId, CancellationToken cancellationToken);

    Task<EchoTarget?> FindByUserMessageAsync(long chatId, int messageId, CancellationToken cancellationToken);

    // False when already cancelled: a double tap or a redelivered update changes nothing.
    Task<bool> CancelAsync(Guid transactionId, CancellationToken cancellationToken);

    // Puts back the status recorded before the latest cancellation. False when the record is not cancelled.
    Task<bool> RestoreAsync(Guid transactionId, CancellationToken cancellationToken);

    // False when this exact reply is already queued: Telegram redelivers an update whose handling failed partway.
    // sentAt is the correction reply's own send instant, so the model's "today" for this correction is the
    // reply's local day, not the original message's (docs/OPEN-QUESTIONS.md P2-2).
    Task<bool> RequestCorrectionAsync(
        Guid transactionId, string instruction, int sourceMessageId, DateTimeOffset sentAt, CancellationToken cancellationToken);

    // False when the text is unchanged, so a redelivered edit queues nothing.
    Task<bool> ReplaceRawTextAsync(Guid transactionId, string rawText, CancellationToken cancellationToken);

    Task AttachPromptAsync(Guid transactionId, int promptMessageId, CancellationToken cancellationToken);
}
