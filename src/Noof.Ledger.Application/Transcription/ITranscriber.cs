namespace Noof.Ledger.Application.Transcription;

public interface ITranscriber
{
    // The words spoken in the audio, trimmed; empty when nothing was heard (V6). A failure is a ModelCallException,
    // classified the way the categorizer's are.
    Task<string> TranscribeAsync(Stream audio, CancellationToken cancellationToken);
}
