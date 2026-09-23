using System.Net;
using System.Text.Json;
using Anthropic.Exceptions;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Noof.Ledger.Ai.Anthropic;
using Noof.Ledger.Application.Categorization;

namespace Noof.Ledger.Ai.Tests.Anthropic;

public class AnthropicTranslatingChatClientTests
{
    static readonly JsonElement NoArguments = JsonDocument.Parse(
        """{"type":"object","additionalProperties":false,"properties":{},"required":[]}""").RootElement.Clone();

    static readonly ChatMessage[] UserTurn = [new(ChatRole.User, "кофе 250")];

    [Fact]
    public async Task A_tool_marked_strict_reaches_the_SDK_adapter_carrying_its_Strict_property()
    {
        var inner = new ScriptedChatClient().Answer(new TextContent("ok"));
        var tool = new SchemaTool("record_spending", "The answer.", NoArguments);

        await new AnthropicTranslatingChatClient(inner).GetResponseAsync(
            UserTurn, new ChatOptions { Tools = [tool] }, TestContext.Current.CancellationToken);

        var sent = inner.Requests.Should().ContainSingle().Subject.Options!.Tools.Should().ContainSingle()
            .Which.Should().BeAssignableTo<AIFunctionDeclaration>().Subject;
        sent.Name.Should().Be("record_spending");
        sent.Description.Should().Be("The answer.");
        sent.JsonSchema.GetRawText().Should().Be(NoArguments.GetRawText());
        // The name the SDK adapter reads (nameof(Anthropic.Models.Messages.Tool.Strict)) - it copies
        // this entry onto the wire tool's "strict" field and ignores every other key it does not know.
        sent.AdditionalProperties.Should().Contain("Strict", true);
    }

    [Fact]
    public async Task A_tool_not_marked_strict_is_passed_on_untouched()
    {
        var inner = new ScriptedChatClient().Answer(new TextContent("ok"));
        var plain = AIFunctionFactory.Create(() => 1, "plain");

        await new AnthropicTranslatingChatClient(inner).GetResponseAsync(
            UserTurn, new ChatOptions { Tools = [plain] }, TestContext.Current.CancellationToken);

        inner.Requests.Should().ContainSingle().Which.Options!.Tools.Should().ContainSingle().Which.Should().BeSameAs(plain);
    }

    [Fact]
    public async Task The_callers_options_are_left_as_they_were()
    {
        var inner = new ScriptedChatClient().Answer(new TextContent("ok"));
        var tool = new SchemaTool("record_spending", "The answer.", NoArguments);
        var options = new ChatOptions { Tools = [tool] };

        await new AnthropicTranslatingChatClient(inner).GetResponseAsync(UserTurn, options, TestContext.Current.CancellationToken);

        options.Tools.Should().ContainSingle().Which.Should().BeSameAs(tool);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, ModelFailureKind.Terminal, false)]
    [InlineData(HttpStatusCode.Unauthorized, ModelFailureKind.Terminal, true)]
    [InlineData(HttpStatusCode.PaymentRequired, ModelFailureKind.Terminal, true)]
    [InlineData(HttpStatusCode.Forbidden, ModelFailureKind.Terminal, true)]
    [InlineData(HttpStatusCode.NotFound, ModelFailureKind.Terminal, false)]
    [InlineData(HttpStatusCode.TooManyRequests, ModelFailureKind.Transient, false)]
    [InlineData((HttpStatusCode)529, ModelFailureKind.Transient, false)]
    public async Task An_API_error_becomes_a_ModelCallException_classified_by_its_status(
        HttpStatusCode statusCode, ModelFailureKind expectedKind, bool expectedAccountLevel)
    {
        var inner = new ScriptedChatClient().Fail(
            new AnthropicApiException("boom") { StatusCode = statusCode, ResponseBody = "" });

        var act = () => new AnthropicTranslatingChatClient(inner).GetResponseAsync(UserTurn, null, TestContext.Current.CancellationToken);

        var thrown = (await act.Should().ThrowAsync<ModelCallException>()).Which;
        thrown.Kind.Should().Be(expectedKind);
        thrown.IsAccountLevel().Should().Be(expectedAccountLevel);
    }

    [Fact]
    public async Task A_transport_failure_the_SDK_wraps_is_transient()
    {
        // The SDK wraps an HttpRequestException from the transport in AnthropicIOException, which is
        // not an HttpRequestException: before this client existed, a dropped connection escaped
        // ICategorizer as a raw SDK exception instead of the ModelCallException it promises.
        var inner = new ScriptedChatClient().Fail(
            new AnthropicIOException("I/O exception", new HttpRequestException("No route to host")));

        var act = () => new AnthropicTranslatingChatClient(inner).GetResponseAsync(UserTurn, null, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<ModelCallException>()).Which.Kind.Should().Be(ModelFailureKind.Transient);
    }

    [Fact]
    public async Task Any_other_SDK_failure_is_transient()
    {
        var inner = new ScriptedChatClient().Fail(new AnthropicInvalidDataException("unreadable response"));

        var act = () => new AnthropicTranslatingChatClient(inner).GetResponseAsync(UserTurn, null, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<ModelCallException>()).Which.Kind.Should().Be(ModelFailureKind.Transient);
    }

    [Fact]
    public async Task A_timeout_is_transient()
    {
        var inner = new ScriptedChatClient().Fail(new TaskCanceledException("The request was canceled due to the configured client timeout."));

        var act = () => new AnthropicTranslatingChatClient(inner).GetResponseAsync(UserTurn, null, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<ModelCallException>()).Which.Kind.Should().Be(ModelFailureKind.Transient);
    }

    [Fact]
    public async Task A_bare_network_failure_is_transient()
    {
        var inner = new ScriptedChatClient().Fail(new HttpRequestException("Connection refused"));

        var act = () => new AnthropicTranslatingChatClient(inner).GetResponseAsync(UserTurn, null, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<ModelCallException>()).Which.Kind.Should().Be(ModelFailureKind.Transient);
    }
}
