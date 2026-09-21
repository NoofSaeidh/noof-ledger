namespace Noof.Ledger.Application.Capture;

public interface ICaptureStore
{
    // Writes the Transaction AND its CategorizationJob inside ONE database transaction.
    // Resolves the wallet itself: the single Wallet with IsDefault = true. No default wallet
    // throws InvalidOperationException - a capture with no wallet cannot be recovered later.
    // Idempotent on (ChatId, MessageId): a replayed Telegram update returns the existing id,
    // writes nothing.
    Task<Guid> CaptureAsync(CapturedMessage message, string timeZoneId, CancellationToken cancellationToken);

    Task AttachBotMessageAsync(Guid transactionId, int botMessageId, CancellationToken cancellationToken);
}
