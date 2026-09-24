using Microsoft.Extensions.AI;
using Noof.Ledger.Application.Transcription;

namespace Noof.Ledger.Ai;

internal sealed class SpeechTranscriber(ISpeechToTextClientFactory clientFactory) : ITranscriber
{
    // The operator speaks Russian with the odd Serbian shop name (2026-09-23). Naming the language beats detecting it
    // from a few seconds of audio.
    const string SpeechLanguage = "ru";

    public async Task<string> TranscribeAsync(Stream audio, CancellationToken cancellationToken)
    {
        using var client = await clientFactory.CreateAsync(cancellationToken);
        var response = await client.GetTextAsync(audio, new SpeechToTextOptions { SpeechLanguage = SpeechLanguage }, cancellationToken);
        return response.Text.Trim();
    }
}
