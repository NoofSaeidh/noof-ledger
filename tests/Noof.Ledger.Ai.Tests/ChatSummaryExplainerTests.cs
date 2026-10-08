using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Application.Reporting.Summary;
using Noof.Ledger.TestKit;

namespace Noof.Ledger.Ai.Tests;

// The summary explainer with no provider underneath it: the tool it offers, the turn it sends and how it reads the
// answer. What reaches the wire is pinned by ChatSummaryExplainerOverAnthropicTests.
public class ChatSummaryExplainerTests
{
    // Shaped like MonthlySummaryText's output; the explainer never parses it, so its exact layout is not under test.
    static readonly string Summary = string.Join('\n',
        "October 2026 · 1–8 Oct · EUR",
        "Spent 412.30 (vs Sep 1–8 +12%, vs 3-month avg -5%)",
        "Received 2100.00 · Net +1687.70 (vs Sep 1–8 +310.00)",
        "• Groceries: 180.10 EUR, 40.00 more than your 3-month average.",
        "Categories: Groceries 180.10 · Cafés 95.00 · Other 22.40",
        "Largest: 89.00 Gigatron · 40.00 Maxi",
        "Rates of 3 Oct–7 Oct · exchangerate-api.com");

    const string Comment = "Groceries cost 40.00 EUR more than usual. Cafés held steady. Watch groceries next month.";

    static readonly IOperationTimer NoopTimer = new OperationTimer(TimeProvider.System, new SlowOperationOptions());

    static readonly IFiscalVerificationUrl VerificationUrl =
        new FiscalVerificationUrl(new FiscalVerificationUrlOptions { VerificationUrlPrefix = "https://suf.purs.gov.rs/v/?vl=" });

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static ChatSummaryExplainer Build(
        IChatClient provider, IOperationTimer? timer = null, ILogger<ChatSummaryExplainer>? logger = null) =>
        new(new FixedChatClientFactory(provider), VerificationUrl, timer ?? NoopTimer,
            logger ?? NullLogger<ChatSummaryExplainer>.Instance);

    static FunctionCallContent Answer(string text = Comment) =>
        new("call_1", "write_summary_comment", new Dictionary<string, object?> { ["text"] = text });

    static string UserTurnOf(ScriptedChatClient provider) => provider.Requests.Single().Messages.Single().Text;

    [Fact]
    public async Task Offers_one_forced_strict_write_summary_comment_tool_in_one_round()
    {
        var provider = new ScriptedChatClient().Answer(Answer());

        await Build(provider).ExplainAsync(new SummaryExplanationRequest(Summary), Ct);

        var options = provider.Requests.Should().ContainSingle().Subject.Options!;
        options.Tools.Should().ContainSingle().Which.Name.Should().Be("write_summary_comment");
        options.Tools![0].IsStrict().Should().BeTrue();
        options.ToolMode.Should().BeOfType<RequiredChatToolMode>().Which.RequiredFunctionName.Should().Be("write_summary_comment");
        options.Instructions.Should().Be(SummaryCommentPrompt.System);
    }

    [Fact]
    public void The_instructions_are_the_specs_words()
    {
        SummaryCommentPrompt.System.ReplaceLineEndings("\n").Should().Be(string.Join('\n',
            "You comment on a person's monthly spending summary. Write three to five short English sentences about what stands",
            "out: the biggest changes, anything unusual, anything worth watching next month. Use only the figures given. Do not",
            "give financial advice and do not repeat the whole summary. Answer with the write_summary_comment tool."));
    }

    [Fact]
    public async Task The_user_turn_is_the_summary_as_it_was_shown()
    {
        var provider = new ScriptedChatClient().Answer(Answer());

        await Build(provider).ExplainAsync(new SummaryExplanationRequest(Summary), Ct);

        UserTurnOf(provider).Should().Be(Summary);
    }

    [Fact]
    public async Task Reads_the_trimmed_text_from_the_tool_call()
    {
        var provider = new ScriptedChatClient().Answer(Answer("  " + Comment + "\n"));

        var comment = await Build(provider).ExplainAsync(new SummaryExplanationRequest(Summary), Ct);

        comment.Should().Be(Comment);
    }

    [Fact]
    public async Task A_fiscal_link_in_the_summary_is_stripped_line_by_line()
    {
        var provider = new ScriptedChatClient().Answer(Answer());
        var withLinks = string.Join('\n',
            "October 2026 · 1–8 Oct · EUR",
            "Largest: 89.00 Gigatron https://suf.purs.gov.rs/v/?vl=LargestVl · 40.00 Maxi",
            "https://suf.purs.gov.rs/v/?vl=WholeLineVl",
            "Top merchants: Maxi 120.40 (6) https://suf.purs.gov.rs/v/?vl=LineEndVl");

        await Build(provider).ExplainAsync(new SummaryExplanationRequest(withLinks), Ct);

        UserTurnOf(provider).Should().Be(string.Join('\n',
            "October 2026 · 1–8 Oct · EUR",
            "Largest: 89.00 Gigatron · 40.00 Maxi",
            "",
            "Top merchants: Maxi 120.40 (6)"));
    }

    [Theory]
    [InlineData("no tool call")]
    [InlineData("empty payload")]
    [InlineData("blank text")]
    public async Task No_tool_call_an_empty_payload_or_a_blank_text_is_a_transient_model_failure(string scenario)
    {
        var provider = new ScriptedChatClient().Answer(Malformed(scenario));

        var act = () => Build(provider).ExplainAsync(new SummaryExplanationRequest(Summary), Ct);

        (await act.Should().ThrowAsync<ModelCallException>()).Which.Kind.Should().Be(ModelFailureKind.Transient);
        provider.Requests.Should().ContainSingle("a malformed answer is not retried inside the explainer");
    }

    [Fact]
    public async Task A_comment_is_timed_as_model_explainSummary()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var logger = new CapturingLogger<ChatSummaryExplainer>();
        var provider = new ScriptedChatClient().AnswerAfterDelay(clock, TimeSpan.FromMilliseconds(10), Answer());

        await Build(provider, new OperationTimer(clock, new SlowOperationOptions()), logger)
            .ExplainAsync(new SummaryExplanationRequest(Summary), Ct);

        logger.Entries.Should().ContainSingle(entry => (string)entry.Properties["Operation"] == "model.explainSummary");
    }

    const string SecretWords = "SummarySecretWords";

    static CapturedLogEntry[] FailuresIn(CapturingLogger<ChatSummaryExplainer> logger) =>
        [.. logger.Entries.Where(entry => entry.EventId.Id == 2101)];

    static void ShouldHaveLoggedOnlyTheFailureType(CapturingLogger<ChatSummaryExplainer> logger, string failureType)
    {
        var failure = FailuresIn(logger).Should().ContainSingle().Subject;
        failure.Level.Should().Be(LogLevel.Warning);
        failure.Properties["FailureType"].Should().Be(failureType);
        failure.Exception.Should().BeNull("the exception's message can quote the summary or the model's text");
        logger.Entries.SelectMany(entry => entry.Properties.Values).OfType<string>()
            .Should().NotContain(value => value.Contains(SecretWords, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_model_failure_is_rethrown_and_logged_with_its_kind_only()
    {
        var logger = new CapturingLogger<ChatSummaryExplainer>();
        var failure = new ModelCallException(ModelFailureKind.Terminal, $"status 401 for {SecretWords}");
        var provider = new ScriptedChatClient().Fail(failure);

        var act = () => Build(provider, logger: logger).ExplainAsync(new SummaryExplanationRequest(Summary + "\n" + SecretWords), Ct);

        (await act.Should().ThrowAsync<ModelCallException>()).Which.Should().BeSameAs(failure);
        ShouldHaveLoggedOnlyTheFailureType(logger, "Terminal");
    }

    [Fact]
    public async Task Any_other_failure_is_rethrown_and_logged_with_its_type_name_only()
    {
        var logger = new CapturingLogger<ChatSummaryExplainer>();
        var provider = new ScriptedChatClient().Fail(new InvalidOperationException($"the model said {SecretWords}"));

        var act = () => Build(provider, logger: logger).ExplainAsync(new SummaryExplanationRequest(Summary), Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
        ShouldHaveLoggedOnlyTheFailureType(logger, "InvalidOperationException");
    }

    [Fact]
    public async Task A_text_that_is_not_a_string_escapes_as_the_JsonException_it_is_and_is_logged_by_type()
    {
        var logger = new CapturingLogger<ChatSummaryExplainer>();
        var provider = new ScriptedChatClient().Answer(new FunctionCallContent(
            "call_1", "write_summary_comment", new Dictionary<string, object?> { ["text"] = 42 }));

        var act = () => Build(provider, logger: logger).ExplainAsync(new SummaryExplanationRequest(Summary), Ct);

        await act.Should().ThrowAsync<System.Text.Json.JsonException>();
        ShouldHaveLoggedOnlyTheFailureType(logger, "JsonException");
    }

    [Fact]
    public async Task A_blank_text_is_logged_as_a_transient_failure()
    {
        var logger = new CapturingLogger<ChatSummaryExplainer>();
        var provider = new ScriptedChatClient().Answer(Answer(" "));

        var act = () => Build(provider, logger: logger).ExplainAsync(new SummaryExplanationRequest(Summary), Ct);

        await act.Should().ThrowAsync<ModelCallException>();
        ShouldHaveLoggedOnlyTheFailureType(logger, "Transient");
    }

    [Fact]
    public async Task Cancellation_is_rethrown_without_a_warning()
    {
        var logger = new CapturingLogger<ChatSummaryExplainer>();
        var provider = new ScriptedChatClient().Fail(new OperationCanceledException());

        var act = () => Build(provider, logger: logger).ExplainAsync(new SummaryExplanationRequest(Summary), Ct);

        await act.Should().ThrowAsync<OperationCanceledException>();
        FailuresIn(logger).Should().BeEmpty();
    }

    [Fact]
    public async Task An_answer_logs_no_failure()
    {
        var logger = new CapturingLogger<ChatSummaryExplainer>();
        var provider = new ScriptedChatClient().Answer(Answer());

        await Build(provider, logger: logger).ExplainAsync(new SummaryExplanationRequest(Summary), Ct);

        FailuresIn(logger).Should().BeEmpty();
    }

    static AIContent Malformed(string scenario) => scenario switch
    {
        "no tool call" => new TextContent("Groceries went up."),
        "empty payload" => new FunctionCallContent("call_1", "write_summary_comment", null),
        "blank text" => Answer("   "),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
    };

    sealed class FixedChatClientFactory(IChatClient client) : IChatClientFactory
    {
        public Task<IChatClient> CreateAsync(CancellationToken cancellationToken) => Task.FromResult(client);
    }
}
