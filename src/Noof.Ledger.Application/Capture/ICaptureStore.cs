namespace Noof.Ledger.Application.Capture;

public interface ICaptureStore
{
    // Writes the Transaction AND its CategorizationJob inside ONE database transaction.
    // Names no wallet: the wallet is chosen when the record is read (the model names one, or the default for the
    // currency is used), so a capture never fails for want of one.
    // Idempotent on (ChatId, MessageId): a replayed Telegram update returns the existing id,
    // writes nothing.
    Task<Guid> CaptureAsync(CapturedMessage message, string timeZoneId, CancellationToken cancellationToken);

    // CaptureAsync for a voice note: no wallet either, the same single database transaction and the same
    // idempotency on (ChatId, MessageId). The record has no text yet, and its job is a Transcribe job (V3).
    Task<Guid> CaptureVoiceAsync(CapturedVoice voice, string timeZoneId, CancellationToken cancellationToken);

    // CaptureAsync for a receipt (Phase 6): no wallet either, the same single database transaction and
    // the same idempotency on (ChatId, MessageId). The caption is stored as the transaction's raw text;
    // its job is an ExtractReceipt job.
    Task<Guid> CaptureReceiptAsync(CapturedReceipt receipt, string timeZoneId, CancellationToken cancellationToken);

    Task AttachBotMessageAsync(Guid transactionId, int botMessageId, CancellationToken cancellationToken);
}
