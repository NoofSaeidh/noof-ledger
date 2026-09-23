namespace Noof.Ledger.Application.Capture;

public interface ICaptureStore
{
    // Writes the Transaction AND its CategorizationJob inside ONE database transaction.
    // Resolves the wallet itself: the single Wallet with IsDefault = true. No default wallet
    // throws InvalidOperationException - a capture with no wallet cannot be recovered later.
    // Idempotent on (ChatId, MessageId): a replayed Telegram update returns the existing id,
    // writes nothing.
    Task<Guid> CaptureAsync(CapturedMessage message, string timeZoneId, CancellationToken cancellationToken);

    // CaptureAsync for a voice note: the same wallet rule, the same single database transaction and the same
    // idempotency on (ChatId, MessageId). The record has no text yet, and its job is a Transcribe job (V3).
    Task<Guid> CaptureVoiceAsync(CapturedVoice voice, string timeZoneId, CancellationToken cancellationToken);

    Task AttachBotMessageAsync(Guid transactionId, int botMessageId, CancellationToken cancellationToken);
}
