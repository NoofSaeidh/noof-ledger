using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Noof.Ledger.Ai.Groq;
using Noof.Ledger.Application.Categorization;

namespace Noof.Ledger.Ai.Tests.Groq;

public class GroqSpeechToTextClientTests
{
    const string ApiKey = "gsk-VERY-SECRET-DO-NOT-LEAK-abc123";
    static readonly byte[] SyntheticAudio = "OggS-synthetic-voice-note"u8.ToArray();

    static GroqSpeechToTextClient Client(HttpMessageHandler handler) => new(new HttpClient(handler), ApiKey, new GroqOptions());

    static Task<SpeechToTextResponse> TranscribeAsync(GroqSpeechToTextClient client) =>
        client.GetTextAsync(new MemoryStream(SyntheticAudio), new SpeechToTextOptions { SpeechLanguage = "ru" },
            TestContext.Current.CancellationToken);

    sealed class ThrowingHttpMessageHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw exception;
    }

    [Fact]
    public async Task Posts_the_note_as_voice_ogg_with_the_model_the_language_and_json()
    {
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, """{"text":"кофе 250"}""");

        await TranscribeAsync(Client(handler));

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.Should().Be(new Uri("https://api.groq.com/openai/v1/audio/transcriptions"));
        request.Headers["Authorization"].Should().Be($"Bearer {ApiKey}");
        request.Body.Should().Contain("name=file; filename=voice.ogg")
            .And.Contain("audio/ogg")
            .And.Contain("OggS-synthetic-voice-note", "the audio bytes go through untouched")
            .And.Contain("whisper-large-v3")
            .And.Contain("name=language").And.Contain("ru")
            .And.Contain("name=response_format").And.Contain("json")
            .And.NotContain(".oga", "Groq refuses Telegram's own extension");
    }

    [Fact]
    public async Task An_operator_override_missing_its_trailing_slash_still_posts_under_its_own_path()
    {
        // GroqOptions.BaseAddress is combined with a relative path via `new Uri(base, relative)`. Uri's own
        // relative-resolution rules treat a base with no trailing slash as ending in a "file" - the last
        // path segment - and drop it, so an override of "https://example.test/openai/v1" would otherwise
        // silently post to ".../openai/audio/transcriptions" instead of ".../openai/v1/audio/transcriptions".
        var options = new GroqOptions();
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["BaseAddress"] = "https://example.test/openai/v1" })
            .Build()
            .Bind(options);
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, """{"text":"кофе 250"}""");

        await TranscribeAsync(new GroqSpeechToTextClient(new HttpClient(handler), ApiKey, options));

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Uri.Should().Be(new Uri("https://example.test/openai/v1/audio/transcriptions"));
    }

    [Fact]
    public async Task Returns_what_groq_heard()
    {
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, """{"text":" купил вчера штуку евро ","x_groq":{"id":"req_1"}}""");

        var response = await TranscribeAsync(Client(handler));

        response.Text.Should().Be(" купил вчера штуку евро ");
        response.ModelId.Should().Be("whisper-large-v3");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, ModelFailureKind.Terminal, false)]
    [InlineData(HttpStatusCode.Unauthorized, ModelFailureKind.Terminal, true)]
    [InlineData(HttpStatusCode.Forbidden, ModelFailureKind.Terminal, true)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, ModelFailureKind.Terminal, false)]
    [InlineData(HttpStatusCode.UnsupportedMediaType, ModelFailureKind.Terminal, false)]
    [InlineData(HttpStatusCode.InternalServerError, ModelFailureKind.Transient, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ModelFailureKind.Transient, false)]
    public async Task A_failed_answer_is_classified(HttpStatusCode status, ModelFailureKind kind, bool accountLevel)
    {
        var handler = new StubHttpMessageHandler().Enqueue(status, """{"error":{"message":"no"}}""");

        var act = () => TranscribeAsync(Client(handler));

        var exception = (await act.Should().ThrowAsync<ModelCallException>()).Which;
        exception.Kind.Should().Be(kind);
        exception.IsAccountLevel().Should().Be(accountLevel);
    }

    [Fact]
    public async Task Too_many_requests_is_transient_and_not_account_level()
    {
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.TooManyRequests, """{"error":{"message":"rate limit"}}""");

        var act = () => TranscribeAsync(Client(handler));

        var exception = (await act.Should().ThrowAsync<ModelCallException>()).Which;
        exception.Kind.Should().Be(ModelFailureKind.Transient, "the free tier allows 20 requests a minute; a burst of notes waits, it does not fail");
        exception.IsAccountLevel().Should().BeFalse("a rate limit is not a broken key");
    }

    [Fact]
    public async Task An_unreachable_groq_is_transient()
    {
        var act = () => TranscribeAsync(Client(new ThrowingHttpMessageHandler(new HttpRequestException("no route"))));

        (await act.Should().ThrowAsync<ModelCallException>()).Which.Kind.Should().Be(ModelFailureKind.Transient);
    }

    [Fact]
    public async Task A_timeout_is_transient()
    {
        var act = () => TranscribeAsync(Client(new ThrowingHttpMessageHandler(new TaskCanceledException("timed out"))));

        (await act.Should().ThrowAsync<ModelCallException>()).Which.Kind.Should().Be(ModelFailureKind.Transient);
    }

    [Fact]
    public async Task An_answer_with_no_text_in_it_is_terminal()
    {
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, """{"unexpected":true}""");

        var act = () => TranscribeAsync(Client(handler));

        (await act.Should().ThrowAsync<ModelCallException>()).Which.Kind.Should().Be(ModelFailureKind.Terminal);
    }

    [Fact]
    public async Task The_key_never_appears_in_a_failure()
    {
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.Unauthorized, $$$"""{"error":{"message":"bad key {{{ApiKey}}}"}}""");

        var act = () => TranscribeAsync(Client(handler));

        var exception = (await act.Should().ThrowAsync<ModelCallException>()).Which;
        exception.ToString().Should().NotContain(ApiKey);
    }
}
