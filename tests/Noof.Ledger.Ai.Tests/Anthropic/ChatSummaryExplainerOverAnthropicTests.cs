using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Noof.Ledger.Ai.Anthropic;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Application.Reporting.Summary;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.TestKit;

namespace Noof.Ledger.Ai.Tests.Anthropic;

// ChatSummaryExplainer end to end over the real Anthropic pipeline, asserted on the HTTP body the SDK actually sent, as
// ChatFindingExplainerOverAnthropicTests does. Contains-checks use ASCII words only: the SDK's JSON encoder escapes
// non-ASCII, apostrophes and '+'.
public class ChatSummaryExplainerOverAnthropicTests
{
    const string VerificationUrlPrefix = "https://suf.purs.gov.rs/v/?vl=";

    const string WriteSummaryCommentAnswer = """
        {"id":"msg_21","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
         "content":[{"type":"tool_use","id":"toolu_21","name":"write_summary_comment","input":{"text":"Groceries cost 40.00 EUR more than usual. Watch them next month."}}],
         "stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":400,"output_tokens":30}}
        """;

    static readonly IOperationTimer Timer = new OperationTimer(TimeProvider.System, new SlowOperationOptions());

    static readonly string Summary = string.Join('\n',
        "October 2026 · 1–8 Oct · EUR",
        "• Groceries: 180.10 EUR, 40.00 more than your 3-month average.",
        "Largest: 89.00 Gigatron · 40.00 Maxi");

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static (ChatSummaryExplainer Explainer, StubHttpMessageHandler Handler) Build(ILogger<ChatSummaryExplainer>? logger = null)
    {
        var handler = new StubHttpMessageHandler();
        var clientFactory = new AnthropicChatClientFactory(
            new StubSecretStore(SecretState.Present, "sk-ant-test-key-do-not-log-me"), new HttpClient(handler),
            new AnthropicOptions { Model = "claude-haiku-4-5-20251001", MaxTokens = 2048, Timeout = TimeSpan.FromSeconds(90) },
            Timer, NullLogger<AnthropicChatClientFactory>.Instance);
        var explainer = new ChatSummaryExplainer(
            clientFactory,
            new FiscalVerificationUrl(new FiscalVerificationUrlOptions { VerificationUrlPrefix = VerificationUrlPrefix }),
            Timer, logger ?? NullLogger<ChatSummaryExplainer>.Instance);
        return (explainer, handler);
    }

    static JsonElement SentBody(StubHttpMessageHandler handler) => JsonDocument.Parse(handler.Requests.Single().Body).RootElement;

    [Fact]
    public async Task Sends_one_forced_strict_write_summary_comment_tool()
    {
        var (explainer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, WriteSummaryCommentAnswer);

        await explainer.ExplainAsync(new SummaryExplanationRequest(Summary), Ct);

        var sent = SentBody(handler);
        var tools = sent.GetProperty("tools");
        tools.GetArrayLength().Should().Be(1);
        tools[0].GetProperty("name").GetString().Should().Be("write_summary_comment");
        tools[0].GetProperty("strict").GetBoolean().Should().BeTrue();
        sent.GetProperty("tool_choice").GetProperty("type").GetString().Should().Be("tool");
        sent.GetProperty("tool_choice").GetProperty("name").GetString().Should().Be("write_summary_comment");
    }

    [Fact]
    public async Task The_tools_schema_requires_exactly_one_text_string()
    {
        var (explainer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, WriteSummaryCommentAnswer);

        await explainer.ExplainAsync(new SummaryExplanationRequest(Summary), Ct);

        var tool = SentBody(handler).GetProperty("tools")[0];
        tool.GetProperty("description").GetString().Should().Be(
            "Write a short English comment on the monthly spending summary for the person who keeps it.");
        var schema = tool.GetProperty("input_schema");
        schema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().Equal("text");
        var properties = schema.GetProperty("properties");
        properties.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["text"]);
        properties.GetProperty("text").GetProperty("type").GetString().Should().Be("string");
        properties.GetProperty("text").GetProperty("description").GetString().Should().Be(
            "The comment, in English, plain text: three to five short sentences on what stands out in the summary.");
    }

    [Fact]
    public async Task The_instructions_and_the_summary_reach_the_wire()
    {
        var (explainer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, WriteSummaryCommentAnswer);

        await explainer.ExplainAsync(new SummaryExplanationRequest(Summary), Ct);

        var body = handler.Requests.Single().Body;
        body.Should().Contain("Use only the figures given.");
        body.Should().Contain("Answer with the write_summary_comment tool.");
        body.Should().Contain("Groceries: 180.10 EUR, 40.00 more than your 3-month average.");
    }

    [Fact]
    public async Task Reads_the_comment_from_the_answer()
    {
        var (explainer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, WriteSummaryCommentAnswer);

        var comment = await explainer.ExplainAsync(new SummaryExplanationRequest(Summary), Ct);

        comment.Should().Be("Groceries cost 40.00 EUR more than usual. Watch them next month.");
    }

    [Fact]
    public async Task No_fiscal_link_in_the_summary_reaches_the_wire()
    {
        var (explainer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, WriteSummaryCommentAnswer);
        var withLinks = string.Join('\n',
            "October 2026 · 1–8 Oct · EUR",
            $"Largest: 89.00 Gigatron {VerificationUrlPrefix}LargestVl · 40.00 Maxi",
            $"{VerificationUrlPrefix}WholeLineVl");

        await explainer.ExplainAsync(new SummaryExplanationRequest(withLinks), Ct);

        var body = handler.Requests.Single().Body;
        body.Should().NotContain("suf.purs.gov.rs");
        body.Should().NotContain("LargestVl").And.NotContain("WholeLineVl");
        body.Should().Contain("89.00 Gigatron").And.Contain("40.00 Maxi");
    }

    [Fact]
    public async Task An_authentication_failure_surfaces_as_a_terminal_model_failure_logged_by_kind()
    {
        var logger = new CapturingLogger<ChatSummaryExplainer>();
        var (explainer, handler) = Build(logger);
        handler.Enqueue(HttpStatusCode.Unauthorized, AnthropicResponses.AuthenticationError);

        var act = () => explainer.ExplainAsync(new SummaryExplanationRequest(Summary), Ct);

        (await act.Should().ThrowAsync<ModelCallException>()).Which.Kind.Should().Be(ModelFailureKind.Terminal);
        var failure = logger.Entries.Should().ContainSingle(entry => entry.EventId.Id == 2101).Subject;
        failure.Properties["FailureType"].Should().Be("Terminal");
        failure.Exception.Should().BeNull();
    }
}
