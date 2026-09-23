using System.Net;
using AwesomeAssertions;
using Noof.Ledger.Ai.Anthropic;
using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Ai.Tests.Anthropic;

public class AnthropicChatClientFactoryTests
{
    static AnthropicChatClientFactory Factory(ISecretStore secretStore, HttpMessageHandler? handler = null) =>
        new(secretStore, new HttpClient(handler ?? new StubHttpMessageHandler()), new AnthropicOptions());

    [Fact]
    public void Its_secret_is_the_one_the_operators_key_is_stored_under_byte_for_byte()
    {
        // Not a name to tidy: the operator's real key is stored encrypted under this string, and
        // EfSecretStore builds the Data Protection purpose from it. One changed character orphans it.
        var secretStore = new StubSecretStore(SecretState.Present, "sk-ant-test");

        Factory(secretStore).SecretKey.Should().Be("anthropic-api-key");
        secretStore.PlaintextReads.Should().BeEmpty("SecretKey is a property read; it must not touch the store");
    }

    [Fact]
    public void The_settings_page_labels_its_secret_as_the_Anthropic_API_key()
    {
        Factory(new StubSecretStore(SecretState.Missing)).SecretLabel.Should().Be("Anthropic API key");
    }

    [Theory]
    [InlineData(SecretState.Present, true)]
    [InlineData(SecretState.Missing, false)]
    [InlineData(SecretState.Unreadable, false)]
    public async Task It_is_configured_only_while_its_key_is_present(SecretState state, bool expected)
    {
        var secretStore = new StubSecretStore(state, "sk-ant-test")
        {
            FailReadsWith = new InvalidOperationException("a readiness check has no business decrypting the key"),
        };

        var configured = await Factory(secretStore).IsConfiguredAsync(TestContext.Current.CancellationToken);

        configured.Should().Be(expected);
        secretStore.StatusReads.Should().Equal("anthropic-api-key");
    }

    [Fact]
    public async Task A_working_key_probes_ok_and_costs_no_tokens()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"data":[{"id":"claude-haiku-4-5-20251001"}],"has_more":false}""");
        var secretStore = new StubSecretStore(SecretState.Present, "sk-ant-test");

        var result = await Factory(secretStore, handler).ProbeAsync(TestContext.Current.CancellationToken);

        result.Ok.Should().BeTrue();
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Body.Should().BeNullOrEmpty("GET /v1/models has no request body");
        secretStore.PlaintextReads.Should().Equal("anthropic-api-key");
    }

    [Fact]
    public async Task A_bad_key_probes_not_ok_with_a_message_and_never_throws()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Unauthorized, AnthropicResponses.AuthenticationError);

        var result = await Factory(new StubSecretStore(SecretState.Present, "sk-ant-bad"), handler)
            .ProbeAsync(TestContext.Current.CancellationToken);

        result.Ok.Should().BeFalse();
        result.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_missing_key_probes_not_ok_without_ever_calling_the_network()
    {
        var handler = new StubHttpMessageHandler();

        var result = await Factory(new StubSecretStore(SecretState.Missing), handler)
            .ProbeAsync(TestContext.Current.CancellationToken);

        result.Ok.Should().BeFalse();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_timed_out_call_probes_not_ok_instead_of_throwing()
    {
        // The SDK's Timeout expiring surfaces as TaskCanceledException - the same type a
        // caller-requested cancellation uses. Uncaught, it escaped into the Blazor circuit and left
        // the settings page's Testing flag stuck true until the page was reloaded.
        var handler = new ThrowingHttpMessageHandler(
            () => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout."));

        var result = await Factory(new StubSecretStore(SecretState.Present, "sk-ant-test"), handler)
            .ProbeAsync(TestContext.Current.CancellationToken);

        result.Ok.Should().BeFalse();
        result.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_network_failure_probes_not_ok_and_says_the_provider_could_not_be_reached()
    {
        // The SDK wraps the transport's HttpRequestException in AnthropicIOException, so a probe that
        // only caught HttpRequestException reported a pulled cable as "failed unexpectedly".
        var handler = new ThrowingHttpMessageHandler(() => new HttpRequestException("No route to host"));

        var result = await Factory(new StubSecretStore(SecretState.Present, "sk-ant-test"), handler)
            .ProbeAsync(TestContext.Current.CancellationToken);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("Could not reach Anthropic");
    }

    [Fact]
    public async Task An_unexpected_exception_while_reading_the_key_probes_not_ok_instead_of_throwing()
    {
        var secretStore = new StubSecretStore(SecretState.Present)
        {
            FailReadsWith = new InvalidOperationException("the key ring went away"),
        };

        var result = await Factory(secretStore).ProbeAsync(TestContext.Current.CancellationToken);

        result.Ok.Should().BeFalse();
        result.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task The_api_key_never_appears_in_the_message_for_an_unexpected_failure()
    {
        const string secretKeyText = "sk-ant-VERY-SECRET-DO-NOT-LEAK-abc123";
        var secretStore = new StubSecretStore(SecretState.Present)
        {
            FailReadsWith = new InvalidOperationException($"boom while using key {secretKeyText}"),
        };

        var result = await Factory(secretStore).ProbeAsync(TestContext.Current.CancellationToken);

        result.Message.Should().NotContain(secretKeyText);
    }

    sealed class ThrowingHttpMessageHandler(Func<Exception> exceptionFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw exceptionFactory();
    }
}
