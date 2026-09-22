using System.Net;
using Anthropic;
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

    [Fact]
    public async Task A_timed_out_call_probes_not_ok_instead_of_throwing()
    {
        // AnthropicOptions.Timeout expiring surfaces as TaskCanceledException - the same type a
        // caller-requested cancellation uses. Before this fix, that type was not caught anywhere in
        // ProbeAsync and escaped into the Blazor circuit, leaving the settings page's Testing flag
        // stuck true and the button disabled until the page was reloaded.
        var handler = new HttpClient(new ThrowingHttpMessageHandler(
            () => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.")));
        var factory = new AnthropicClientFactory(
            new StubSecretStore(SecretState.Present, "sk-ant-test"), handler, new AnthropicOptions());
        var probe = new AnthropicKeyProbe(factory);

        var result = await probe.ProbeAsync(TestContext.Current.CancellationToken);

        result.Ok.Should().BeFalse();
        result.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task An_unexpected_exception_from_the_client_factory_probes_not_ok_instead_of_throwing()
    {
        // IAnthropicClientFactory.CreateAsync throws InvalidOperationException for an unhandled
        // SecretState - a defect this class already had a TODO comment about, and exactly the kind
        // of surprise a "never throw" probe contract exists to absorb.
        var factory = new ThrowingClientFactory(new InvalidOperationException("unhandled SecretState value"));
        var probe = new AnthropicKeyProbe(factory);

        var result = await probe.ProbeAsync(TestContext.Current.CancellationToken);

        result.Ok.Should().BeFalse();
        result.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task The_api_key_never_appears_in_the_message_for_an_unexpected_failure()
    {
        const string secretKeyText = "sk-ant-VERY-SECRET-DO-NOT-LEAK-abc123";
        var factory = new ThrowingClientFactory(
            new InvalidOperationException($"boom while using key {secretKeyText}"));
        var probe = new AnthropicKeyProbe(factory);

        var result = await probe.ProbeAsync(TestContext.Current.CancellationToken);

        result.Message.Should().NotContain(secretKeyText);
    }

    static void JsonDocumentBodyShouldBeEmpty(string body) => body.Should().BeNullOrEmpty("GET /v1/models has no request body");

    sealed class ThrowingHttpMessageHandler(Func<Exception> exceptionFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw exceptionFactory();
    }

    sealed class ThrowingClientFactory(Exception exception) : IAnthropicClientFactory
    {
        public Task<AnthropicClient> CreateAsync(CancellationToken cancellationToken) => throw exception;
    }

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
