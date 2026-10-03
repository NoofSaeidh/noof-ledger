using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Noof.Ledger.Ai.Anthropic;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Diagnostics.Integrity;
using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Ai.Tests.Anthropic;

// ChatFindingExplainer end to end over the real Anthropic pipeline, asserted on the HTTP body the SDK actually sent -
// the same pattern ChatReceiptCategorizerOverAnthropicTests uses.
public class ChatFindingExplainerOverAnthropicTests
{
    static readonly IOperationTimer Timer = new OperationTimer(TimeProvider.System, new SlowOperationOptions());

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static (ChatFindingExplainer Explainer, StubHttpMessageHandler Handler) Build(ILogger<ChatFindingExplainer>? logger = null)
    {
        var handler = new StubHttpMessageHandler();
        var clientFactory = new AnthropicChatClientFactory(
            new StubSecretStore(SecretState.Present, "sk-ant-test-key-do-not-log-me"), new HttpClient(handler),
            new AnthropicOptions { Model = "claude-haiku-4-5-20251001", MaxTokens = 2048, Timeout = TimeSpan.FromSeconds(90) },
            Timer, NullLogger<AnthropicChatClientFactory>.Instance);
        var explainer = new ChatFindingExplainer(clientFactory, Timer, logger ?? NullLogger<ChatFindingExplainer>.Instance);
        return (explainer, handler);
    }

    static ExplanationRequest Request() => new(
        "Checks:\n- Not applied: Something has waited on the operator for more than a day.\n\n"
        + "Findings:\n1. Not applied (Waiting on you)\n   Waiting for: A reply to the echo",
        "the amount is wrong",
        "Record: Expense · Failed · captured from Text\nText: exchanged 100 eur");

    static JsonElement SentBody(StubHttpMessageHandler handler) => JsonDocument.Parse(handler.Requests.Single().Body).RootElement;

    [Fact]
    public async Task Sends_one_forced_strict_write_explanation_tool()
    {
        var (explainer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.WriteExplanationJsonAnswer);

        await explainer.ExplainAsync(Request(), Ct);

        var sent = SentBody(handler);
        var tools = sent.GetProperty("tools");
        tools.GetArrayLength().Should().Be(1);
        tools[0].GetProperty("name").GetString().Should().Be("write_explanation");
        tools[0].GetProperty("strict").GetBoolean().Should().BeTrue();
        sent.GetProperty("tool_choice").GetProperty("type").GetString().Should().Be("tool");
        sent.GetProperty("tool_choice").GetProperty("name").GetString().Should().Be("write_explanation");
    }

    [Fact]
    public async Task The_tools_schema_requires_exactly_text_and_looks_like_bug()
    {
        var (explainer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.WriteExplanationJsonAnswer);

        await explainer.ExplainAsync(Request(), Ct);

        var schema = SentBody(handler).GetProperty("tools")[0].GetProperty("input_schema");
        schema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        schema.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("text", "looks_like_bug");
        var properties = schema.GetProperty("properties");
        properties.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["text", "looks_like_bug"]);
        properties.GetProperty("text").GetProperty("type").GetString().Should().Be("string");
        properties.GetProperty("text").GetProperty("description").GetString().Should().Be(
            "The explanation for the operator, in English, plain text: what is wrong, the likely cause, and what to do.");
        properties.GetProperty("looks_like_bug").GetProperty("type").GetString().Should().Be("boolean");
        properties.GetProperty("looks_like_bug").GetProperty("description").GetString().Should().Be(
            "true only when the likely cause is the app's own code rather than the operator's data or a misread phrase.");
    }

    [Fact]
    public async Task The_instructions_the_words_the_record_and_the_findings_reach_the_wire()
    {
        var (explainer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.WriteExplanationJsonAnswer);

        await explainer.ExplainAsync(Request(), Ct);

        var body = handler.Requests.Single().Body;
        body.Should().Contain("never compute, convert or invent one");
        body.Should().Contain("the amount is wrong");
        body.Should().Contain("exchanged 100 eur");
        body.Should().Contain("A reply to the echo");
    }

    [Fact]
    public async Task Reads_the_explanation_from_the_answer()
    {
        var (explainer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.WriteExplanationJsonAnswer);

        var explanation = await explainer.ExplainAsync(Request(), Ct);

        explanation.Should().Be(new Explanation(
            "The exchange is waiting for the amount you received. Reply to the echo with it.", LooksLikeBug: false));
    }

    [Fact]
    public async Task Reads_looks_like_bug_true()
    {
        var (explainer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, """
            {"id":"msg_14","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
             "content":[{"type":"tool_use","id":"toolu_14","name":"write_explanation","input":{"text":"The entries disagree with the lines. This is a bug - file it.","looks_like_bug":true}}],
             "stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":300,"output_tokens":30}}
            """);

        var explanation = await explainer.ExplainAsync(Request(), Ct);

        explanation.LooksLikeBug.Should().BeTrue();
        explanation.Text.Should().Be("The entries disagree with the lines. This is a bug - file it.");
    }
}
