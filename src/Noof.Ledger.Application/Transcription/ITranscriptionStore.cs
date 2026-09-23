namespace Noof.Ledger.Application.Transcription;

public interface ITranscriptionStore
{
    // Writes the transcript as the record's raw_text and queues its first reading, in ONE database transaction.
    // False when the record already has text: a Transcribe job re-run after its commit must not read the note twice.
    Task<bool> CompleteCaptureAsync(Guid transactionId, string transcript, CancellationToken cancellationToken);

    // Queues the correction a spoken reply asked for, with the transcript as its instruction. False when this reply's
    // correction is already queued.
    Task<bool> CompleteCorrectionAsync(
        Guid transactionId, string transcript, int sourceMessageId, DateOnly? instructionDay, CancellationToken cancellationToken);
}
