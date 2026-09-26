using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Ai.Groq;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.TestKit;

namespace Noof.Ledger.Ai.Tests.Groq;

public class GroqSpeechToTextClientFactoryTests
{
    const string ApiKey = "gsk-VERY-SECRET-DO-NOT-LEAK-abc123";

    static GroqSpeechToTextClientFactory Factory(ISecretStore secretStore, HttpMessageHandler? handler = null) =>
        new(secretStore, new HttpClient(handler ?? new StubHttpMessageHandler()), new GroqOptions(),
            new OperationTimer(TimeProvider.System, new SlowOperationOptions()), NullLogger<GroqSpeechToTextClientFactory>.Instance);

    sealed class ThrowingHttpMessageHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw exception;
    }

    [Fact]
    public void The_secret_key_is_exactly_groq_api_key()
    {
        // Data Protection's purpose is derived from this string; changing it orphans the operator's stored key.
        Factory(new StubSecretStore(SecretState.Missing)).SecretKey.Should().Be("groq-api-key");
    }

    [Fact]
    public void The_settings_page_calls_it_the_Groq_API_key()
    {
        Factory(new StubSecretStore(SecretState.Missing)).SecretLabel.Should().Be("Groq API key");
    }

    [Theory]
    [InlineData(SecretState.Present, true)]
    [InlineData(SecretState.Missing, false)]
    [InlineData(SecretState.Unreadable, false)]
    public async Task Is_configured_only_when_a_readable_key_is_stored(SecretState state, bool configured)
    {
        var secretStore = new StubSecretStore(state, state == SecretState.Present ? ApiKey : null);

        (await Factory(secretStore).IsConfiguredAsync(TestContext.Current.CancellationToken)).Should().Be(configured);
        secretStore.PlaintextReads.Should().BeEmpty("asking whether a key is set never decrypts it");
    }

    [Fact]
    public async Task The_probe_lists_models_with_the_key_and_reports_success()
    {
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, """{"data":[]}""");

        var result = await Factory(new StubSecretStore(SecretState.Present, ApiKey), handler).ProbeAsync(TestContext.Current.CancellationToken);

        result.Ok.Should().BeTrue();
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.Uri.Should().Be(new Uri("https://api.groq.com/openai/v1/models"));
        request.Headers["Authorization"].Should().Be($"Bearer {ApiKey}");
    }

    [Fact]
    public async Task The_probe_reports_a_rejected_key()
    {
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.Unauthorized, """{"error":{}}""");

        var result = await Factory(new StubSecretStore(SecretState.Present, ApiKey), handler).ProbeAsync(TestContext.Current.CancellationToken);

        result.Ok.Should().BeFalse();
        result.Message.Should().Be("Groq rejected the key.");
    }

    [Fact]
    public async Task The_probe_without_a_key_never_calls_groq()
    {
        var handler = new StubHttpMessageHandler();

        var result = await Factory(new StubSecretStore(SecretState.Missing), handler).ProbeAsync(TestContext.Current.CancellationToken);

        result.Ok.Should().BeFalse();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failing_probe_still_logs_a_speech_probe_timing()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var timer = new OperationTimer(clock, new SlowOperationOptions());
        var logger = new CapturingLogger<GroqSpeechToTextClientFactory>();
        var handler = new ThrowingHttpMessageHandler(new InvalidOperationException("boom"));
        var factory = new GroqSpeechToTextClientFactory(
            new StubSecretStore(SecretState.Present, ApiKey), new HttpClient(handler), new GroqOptions(), timer, logger);

        var result = await factory.ProbeAsync(TestContext.Current.CancellationToken);

        result.Ok.Should().BeFalse();
        logger.Entries.Should().ContainSingle(entry => (string)entry.Properties["Operation"] == "speech.probe");
    }

    [Fact]
    public async Task The_probe_never_echoes_the_key()
    {
        var handler = new ThrowingHttpMessageHandler(new InvalidOperationException($"boom {ApiKey}"));

        var result = await Factory(new StubSecretStore(SecretState.Present, ApiKey), handler).ProbeAsync(TestContext.Current.CancellationToken);

        result.Ok.Should().BeFalse();
        result.Message.Should().NotContain(ApiKey);
    }

    [Fact]
    public async Task A_client_without_a_key_is_a_terminal_failure()
    {
        var act = () => Factory(new StubSecretStore(SecretState.Missing)).CreateAsync(TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<ModelCallException>()).Which.Kind.Should().Be(ModelFailureKind.Terminal);
    }
}
