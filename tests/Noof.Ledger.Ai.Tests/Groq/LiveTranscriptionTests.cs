using AwesomeAssertions;
using Noof.Ledger.Ai.Groq;
using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Ai.Tests.Groq;

// Opt-in, and only with the operator's permission: it sends their own voice note to Groq. Skipped unless both
// variables are set. The file lives outside the repository - no real voice is ever committed.
public class LiveTranscriptionTests
{
    [Fact]
    public async Task A_real_voice_note_comes_back_as_text()
    {
        if (!LiveTranscriptionGate.TryGet(out var apiKey, out var voiceFile))
            Assert.Skip(LiveTranscriptionGate.SkipMessage);

        var factory = new GroqSpeechToTextClientFactory(
            new StubSecretStore(SecretState.Present, apiKey), new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, new GroqOptions());
        var transcriber = new SpeechTranscriber(factory);

        await using var audio = File.OpenRead(voiceFile);
        var text = await transcriber.TranscribeAsync(audio, TestContext.Current.CancellationToken);

        text.Should().NotBeEmpty();
        TestContext.Current.SendDiagnosticMessage($"Heard: {text}");
    }
}
