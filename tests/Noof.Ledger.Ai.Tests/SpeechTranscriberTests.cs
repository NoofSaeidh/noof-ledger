using AwesomeAssertions;
using Microsoft.Extensions.AI;

namespace Noof.Ledger.Ai.Tests;

public class SpeechTranscriberTests
{
    sealed class ScriptedSpeechToTextClient(string text) : ISpeechToTextClient
    {
        public SpeechToTextOptions? LastOptions { get; private set; }
        public byte[]? LastAudio { get; private set; }
        public bool Disposed { get; private set; }

        public async Task<SpeechToTextResponse> GetTextAsync(
            Stream audioSpeechStream, SpeechToTextOptions? options = null, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await audioSpeechStream.CopyToAsync(buffer, cancellationToken);
            LastAudio = buffer.ToArray();
            LastOptions = options;
            return new SpeechToTextResponse(text);
        }

        public IAsyncEnumerable<SpeechToTextResponseUpdate> GetStreamingTextAsync(
            Stream audioSpeechStream, SpeechToTextOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => Disposed = true;
    }

    sealed class FixedSpeechToTextClientFactory(ISpeechToTextClient client) : ISpeechToTextClientFactory
    {
        public Task<ISpeechToTextClient> CreateAsync(CancellationToken cancellationToken) => Task.FromResult(client);
    }

    static readonly byte[] SyntheticAudio = "OggS-synthetic-voice-note"u8.ToArray();

    [Fact]
    public async Task Asks_for_russian_and_returns_the_trimmed_transcript()
    {
        var client = new ScriptedSpeechToTextClient("  купил вчера штуку евро \n");
        var transcriber = new SpeechTranscriber(new FixedSpeechToTextClientFactory(client));

        var text = await transcriber.TranscribeAsync(new MemoryStream(SyntheticAudio), TestContext.Current.CancellationToken);

        text.Should().Be("купил вчера штуку евро");
        client.LastOptions!.SpeechLanguage.Should().Be("ru");
        client.LastAudio.Should().Equal(SyntheticAudio);
    }

    [Fact]
    public async Task Whitespace_is_nothing_heard()
    {
        var transcriber = new SpeechTranscriber(new FixedSpeechToTextClientFactory(new ScriptedSpeechToTextClient(" \n\t ")));

        var text = await transcriber.TranscribeAsync(new MemoryStream(SyntheticAudio), TestContext.Current.CancellationToken);

        text.Should().BeEmpty();
    }

    [Fact]
    public async Task Disposes_the_client_it_asked_for()
    {
        var client = new ScriptedSpeechToTextClient("кофе");
        var transcriber = new SpeechTranscriber(new FixedSpeechToTextClientFactory(client));

        await transcriber.TranscribeAsync(new MemoryStream(SyntheticAudio), TestContext.Current.CancellationToken);

        client.Disposed.Should().BeTrue();
    }
}
