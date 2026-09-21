using System.Net;
using AwesomeAssertions;
using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Ai.Tests;

public class AnthropicKeyProbeTests
{
    [Fact]
    public void SecretKey_is_the_Anthropic_api_key()
    {
        var probe = new AnthropicKeyProbe(new AnthropicClientFactory(
            new ThrowingSecretStore(), new HttpClient(new StubHttpMessageHandler()), new AnthropicOptions()));

        probe.SecretKey.Should().Be(SecretKeys.AnthropicApiKey);
    }

    [Fact]
    public async Task A_working_key_probes_ok_and_costs_no_tokens()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"data":[{"id":"claude-haiku-4-5-20251001"}],"has_more":false}""");
        var factory = new AnthropicClientFactory(
            new StubSecretStore(SecretState.Present, "sk-ant-test"), new HttpClient(handler), new AnthropicOptions());
        var probe = new AnthropicKeyProbe(factory);

        var result = await probe.ProbeAsync(TestContext.Current.CancellationToken);

        result.Ok.Should().BeTrue();
        handler.Requests.Should().ContainSingle();
        JsonDocumentBodyShouldBeEmpty(handler.Requests[0].Body);
    }

    [Fact]
    public async Task A_bad_key_probes_not_ok_with_a_message_and_never_throws()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Unauthorized, AnthropicResponses.AuthenticationError);
        var factory = new AnthropicClientFactory(
            new StubSecretStore(SecretState.Present, "sk-ant-bad"), new HttpClient(handler), new AnthropicOptions());
        var probe = new AnthropicKeyProbe(factory);

        var result = await probe.ProbeAsync(TestContext.Current.CancellationToken);

        result.Ok.Should().BeFalse();
        result.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_missing_key_probes_not_ok_without_ever_calling_the_network()
    {
        var handler = new StubHttpMessageHandler();
        var factory = new AnthropicClientFactory(
            new StubSecretStore(SecretState.Missing, null), new HttpClient(handler), new AnthropicOptions());
        var probe = new AnthropicKeyProbe(factory);

        var result = await probe.ProbeAsync(TestContext.Current.CancellationToken);

        result.Ok.Should().BeFalse();
        handler.Requests.Should().BeEmpty();
    }

    static void JsonDocumentBodyShouldBeEmpty(string body) => body.Should().BeNullOrEmpty("GET /v1/models has no request body");

    sealed class StubSecretStore(SecretState state, string? value) : ISecretStore
    {
        public Task<SecretResult> GetAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(new SecretResult(state, value));
        public Task<SecretStatus> GetStatusAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task SetAsync(string key, string plaintext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<bool> TrySetIfMissingAsync(string key, string plaintext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    sealed class ThrowingSecretStore : ISecretStore
    {
        public Task<SecretResult> GetAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException("SecretKey is a property read; it must not touch the store at all");
        public Task<SecretStatus> GetStatusAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task SetAsync(string key, string plaintext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<bool> TrySetIfMissingAsync(string key, string plaintext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
