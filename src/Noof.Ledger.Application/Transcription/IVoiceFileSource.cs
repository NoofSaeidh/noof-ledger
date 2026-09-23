namespace Noof.Ledger.Application.Transcription;

public interface IVoiceFileSource
{
    // The voice note's audio, read into memory and positioned at its start; the caller disposes it. A failed
    // download propagates: the worker treats it as transient (V9).
    Task<Stream> DownloadAsync(string voiceFileId, CancellationToken cancellationToken);
}
