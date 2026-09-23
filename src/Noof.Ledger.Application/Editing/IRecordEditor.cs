namespace Noof.Ledger.Application.Editing;

// EchoMessageId is null only when the acknowledgement could not be sent at capture time.
public sealed record EchoTarget(Guid TransactionId, int? EchoMessageId);

public interface IRecordEditor
{
    Task<EchoTarget?> FindByBotMessageAsync(long chatId, int messageId, CancellationToken cancellationToken);

    // False when already cancelled: a double tap or a redelivered update changes nothing.
    Task<bool> CancelAsync(Guid transactionId, CancellationToken cancellationToken);

    // Puts back the status recorded before the latest cancellation. False when the record is not cancelled.
    Task<bool> RestoreAsync(Guid transactionId, CancellationToken cancellationToken);
}
