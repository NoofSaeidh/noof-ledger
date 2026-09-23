using System.Globalization;
using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Noof.Ledger.Ai.Anthropic;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Ai.Tests.Anthropic;

// The provider-neutral categoriser end to end over the real Anthropic pipeline - factory,
// translating client, the SDK's own adapter - asserted on the HTTP body the SDK actually sent.
public class ChatCategorizerOverAnthropicTests
{
    static readonly IReadOnlyList<CategoryOption> Categories =
        [new CategoryOption("food-drink", "Food & Drink", "Еда и напитки", null)];

    static readonly IReadOnlyList<MerchantOption> NoMerchantHints = [];
    static readonly DateOnly Today = new(2026, 9, 22);

    static CategorizationRequest Request(
        string rawText, IReadOnlyList<MerchantOption>? hints = null, IReadOnlyList<MerchantOption>? all = null) =>
        new(rawText, Today, Categories, hints ?? NoMerchantHints, all ?? NoMerchantHints);

    static (ChatCategorizer Categorizer, StubHttpMessageHandler Handler) Build(
        SecretState state = SecretState.Present, string? key = "sk-ant-test-key-do-not-log-me", int maxTokens = 2048)
    {
        var handler = new StubHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var secretStore = new StubSecretStore(state, key);
        var options = new AnthropicOptions { Model = "claude-haiku-4-5-20251001", MaxTokens = maxTokens, Timeout = TimeSpan.FromSeconds(90) };
        var clientFactory = new AnthropicChatClientFactory(secretStore, httpClient, options);
        return (new ChatCategorizer(clientFactory), handler);
    }

    [Fact]
    public async Task Returns_the_proposal_when_the_first_turn_answers_the_record_spending_tool_call()
    {
        var (categorizer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.RecordSpendingJsonAnswer);
        var request = Request("Coffee 3.50 EUR");

        var proposal = await categorizer.ProposeAsync(request, TestContext.Current.CancellationToken);

        proposal.Items.Should().ContainSingle();
        proposal.Items[0].Description.Should().Be("Coffee");
        proposal.Items[0].Amount.Should().Be(3.50m);
        proposal.Items[0].CurrencyCode.Should().Be("EUR", "the JSON field is \"currency\", not \"currency_code\" — this is the mapping Locked Decision 5 exists for");
        proposal.Items[0].CategorySlug.Should().Be("food-drink");
        handler.Requests.Should().ContainSingle("a direct record_spending call on the first turn must not trigger a follow-up call");
    }

    [Fact]
    public async Task Sends_both_tools_strict_and_forced_with_no_output_config_on_the_first_call()
    {
        var (categorizer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.RecordSpendingJsonAnswer);
        var request = Request("Coffee 3.50 EUR");

        await categorizer.ProposeAsync(request, TestContext.Current.CancellationToken);

        var sent = JsonDocument.Parse(handler.Requests[0].Body).RootElement;
        sent.TryGetProperty("output_config", out _).Should().BeFalse("this is a tool call, not a structured-output answer");

        var tools = sent.GetProperty("tools");
        tools.GetArrayLength().Should().Be(2, "guarding against the RawRepresentationFactory trap: a tool must never appear twice");
        var names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString());
        names.Should().BeEquivalentTo(["list_merchants", "record_spending"]);
        foreach (var tool in tools.EnumerateArray())
            tool.GetProperty("strict").GetBoolean().Should().BeTrue($"{tool.GetProperty("name").GetString()} must be strict");

        var recordSpending = tools.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "record_spending");
        var currencyEnum = recordSpending.GetProperty("input_schema")
            .GetProperty("properties").GetProperty("items").GetProperty("items").GetProperty("properties")
            .GetProperty("currency").GetProperty("enum").EnumerateArray().Select(e => e.GetString());
        currencyEnum.Should().BeEquivalentTo(["EUR", "RSD", "USD", "RUB", "KZT", null]);

        sent.GetProperty("tool_choice").GetProperty("type").GetString().Should().Be("any");

        sent.TryGetProperty("temperature", out _).Should().BeFalse("ChatOptions.Temperature must never be set");
    }

    [Fact]
    public async Task No_request_carries_an_anthropic_beta_header()
    {
        var (categorizer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.RecordSpendingJsonAnswer);
        var request = Request("Coffee 3.50 EUR");

        await categorizer.ProposeAsync(request, TestContext.Current.CancellationToken);

        handler.Requests[0].Headers.Should().NotContainKey("anthropic-beta",
            "structured outputs and tool use are GA — no beta header anywhere");
    }

    [Fact]
    public async Task Answers_list_merchants_then_sends_one_follow_up_forced_onto_record_spending()
    {
        var (categorizer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.ListMerchantsToolUse);
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.RecordSpendingJsonAnswer);
        var hints = new List<MerchantOption> { new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Lidl") };
        var all = new List<MerchantOption>
        {
            new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Lidl"),
            new(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Maxi"),
        };
        var request = Request("Lidl 3.50 EUR", hints, all);

        var proposal = await categorizer.ProposeAsync(request, TestContext.Current.CancellationToken);

        proposal.Items.Should().ContainSingle();
        handler.Requests.Should().HaveCount(2, "the loop runs exactly once: one first call, one follow-up, never a third");

        var secondSent = JsonDocument.Parse(handler.Requests[1].Body).RootElement;
        var secondTools = secondSent.GetProperty("tools");
        secondTools.GetArrayLength().Should().Be(1, "only record_spending is offered, which is what structurally rules out a third list_merchants call");
        secondTools[0].GetProperty("name").GetString().Should().Be("record_spending");
        secondTools[0].GetProperty("strict").GetBoolean().Should().BeTrue(
            "FunctionInvokingChatClient strips every tool from this last request and AnswerToolGuard puts record_spending back - it must come back strict");
        secondSent.GetProperty("tool_choice").GetProperty("type").GetString().Should().Be("tool");
        secondSent.GetProperty("tool_choice").GetProperty("name").GetString().Should().Be("record_spending");

        // The answer is the FULL directory. "Maxi" is deliberately absent from the hints, so a
        // regression that answers with request.MerchantHints instead of request.AllMerchants makes
        // this line fail - and nothing else in the suite would have noticed.
        handler.Requests[1].Body.Should().Contain("Maxi");
    }

    [Fact]
    public async Task Maps_an_amount_read_from_words_and_a_merchant_name()
    {
        var (categorizer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.RecordSpendingFromWordsAnswer);
        var request = Request("купил штуку евро в Lidl");

        var proposal = await categorizer.ProposeAsync(request, TestContext.Current.CancellationToken);

        var item = proposal.Items.Should().ContainSingle().Subject;
        item.Amount.Should().Be(1000m);
        item.CurrencyCode.Should().Be("EUR");
        item.MerchantName.Should().Be("Lidl");
    }

    [Fact]
    public async Task Tells_the_model_today_and_maps_the_day_it_answers()
    {
        var (categorizer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.RecordSpendingWithDateAnswer);

        var proposal = await categorizer.ProposeAsync(Request("купил вчера штуку евро"), TestContext.Current.CancellationToken);

        handler.Requests[0].Body.Should().Contain("Today: 2026-09-22 (Tuesday)");
        proposal.OccurredOn.Should().Be("2026-09-21");
    }

    [Theory]
    [InlineData("1000", "1000")]
    [InlineData("45.3", "45.3")]
    [InlineData("0.1", "0.1")]
    // Neither of these two survives a round trip through double: Decimal(double) rounds to 15
    // significant digits, so a production path that read GetDouble() then cast to decimal would
    // still pass the three rows above but corrupt these.
    [InlineData("12345678901234567.89", "12345678901234567.89")]
    [InlineData("0.30000000000000004", "0.30000000000000004")]
    public async Task An_amount_in_the_response_round_trips_into_decimal_exactly(string raw, string expected)
    {
        // The schema declares amount as a JSON number (not a string), and the tool call's arguments
        // are read as a JsonElement with no object converter, so System.Text.Json reads the decimal
        // straight from the response's own token text - no double, no separator handling. raw is
        // spliced into the JSON verbatim (never interpolated through a numeric ToString(), which is
        // culture-sensitive and would emit "45,3" on a machine whose culture uses a comma decimal
        // separator, breaking the JSON for a reason unrelated to what this test guards) (operator,
        // 2026-09-23).
        var (categorizer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, $$$"""
            {"id":"msg_07","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
             "content":[{"type":"tool_use","id":"toolu_07","name":"record_spending","input":{"items":[{"description":"кофе","amount":{{{raw}}},"currency":null,"category_slug":"food-drink","merchant_name":null}]}}],
             "stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":5}}
            """);
        var request = Request("кофе");

        var proposal = await categorizer.ProposeAsync(request, TestContext.Current.CancellationToken);

        proposal.Items.Should().ContainSingle().Which.Amount.Should().Be(decimal.Parse(expected, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task A_model_that_asks_for_the_merchant_list_twice_never_gets_a_third_request()
    {
        var (categorizer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.ListMerchantsToolUse);
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.ListMerchantsToolUse);
        var request = Request("Lidl 3.50 EUR");

        var act = () => categorizer.ProposeAsync(request, TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<ModelCallException>();
        assertion.Which.Kind.Should().Be(ModelFailureKind.Transient);
        handler.Requests.Should().HaveCount(2,
            "exactly two requests were queued and consumed; a third would have hit StubHttpMessageHandler's empty-queue guard and thrown InvalidOperationException instead of ModelCallException");
    }

    [Fact]
    public async Task Neither_JSON_nor_a_known_tool_call_on_the_first_turn_is_a_transient_failure()
    {
        var (categorizer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.NoAnswerAtAll);
        var request = Request("what is this");

        var act = () => categorizer.ProposeAsync(request, TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<ModelCallException>();
        assertion.Which.Kind.Should().Be(ModelFailureKind.Transient);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, ModelFailureKind.Terminal)]
    [InlineData(HttpStatusCode.Unauthorized, ModelFailureKind.Terminal)]
    [InlineData(HttpStatusCode.PaymentRequired, ModelFailureKind.Terminal)]
    [InlineData(HttpStatusCode.Forbidden, ModelFailureKind.Terminal)]
    [InlineData(HttpStatusCode.NotFound, ModelFailureKind.Terminal)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, ModelFailureKind.Terminal)]
    [InlineData(HttpStatusCode.RequestTimeout, ModelFailureKind.Transient)]
    [InlineData(HttpStatusCode.Conflict, ModelFailureKind.Transient)]
    [InlineData(HttpStatusCode.TooManyRequests, ModelFailureKind.Transient)]
    [InlineData(HttpStatusCode.InternalServerError, ModelFailureKind.Transient)]
    [InlineData((HttpStatusCode)529, ModelFailureKind.Transient)]
    [InlineData((HttpStatusCode)599, ModelFailureKind.Transient)]
    public async Task Classifies_every_Anthropic_status_code_per_the_locked_table(HttpStatusCode statusCode, ModelFailureKind expectedKind)
    {
        var (categorizer, handler) = Build();
        handler.Enqueue(statusCode, AnthropicResponses.GenericError("some_error", "boom"));
        var request = Request("Coffee 3.50 EUR");

        var act = () => categorizer.ProposeAsync(request, TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<ModelCallException>();
        assertion.Which.Kind.Should().Be(expectedKind, $"status {(int)statusCode} must classify as {expectedKind}");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.PaymentRequired)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_401_402_or_403_is_marked_account_level_so_one_bad_key_cannot_be_mistaken_for_a_per_job_failure(
        HttpStatusCode statusCode)
    {
        var (categorizer, handler) = Build();
        handler.Enqueue(statusCode, AnthropicResponses.GenericError("some_error", "boom"));
        var request = Request("Coffee 3.50 EUR");

        var act = () => categorizer.ProposeAsync(request, TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<ModelCallException>();
        assertion.Which.Kind.Should().Be(ModelFailureKind.Terminal, "the locked status table is unchanged");
        assertion.Which.IsAccountLevel().Should().BeTrue(
            "401/402/403 are properties of the account, not of this job's request");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    public async Task A_genuinely_per_job_terminal_status_is_not_marked_account_level(HttpStatusCode statusCode)
    {
        var (categorizer, handler) = Build();
        handler.Enqueue(statusCode, AnthropicResponses.GenericError("some_error", "boom"));
        var request = Request("Coffee 3.50 EUR");

        var act = () => categorizer.ProposeAsync(request, TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<ModelCallException>();
        assertion.Which.Kind.Should().Be(ModelFailureKind.Terminal);
        assertion.Which.IsAccountLevel().Should().BeFalse();
    }

    [Fact]
    public async Task A_missing_key_is_a_terminal_failure_without_ever_calling_the_network()
    {
        var (categorizer, handler) = Build(state: SecretState.Missing, key: null);
        var request = Request("Coffee 3.50 EUR");

        var act = () => categorizer.ProposeAsync(request, TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<ModelCallException>();
        assertion.Which.Kind.Should().Be(ModelFailureKind.Terminal);
        handler.Requests.Should().BeEmpty("a missing key must fail before any HTTP call is attempted");
    }

    [Fact]
    public async Task An_unreadable_key_is_a_terminal_failure_without_ever_calling_the_network()
    {
        var (categorizer, handler) = Build(state: SecretState.Unreadable, key: null);
        var request = Request("Coffee 3.50 EUR");

        var act = () => categorizer.ProposeAsync(request, TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<ModelCallException>();
        assertion.Which.Kind.Should().Be(ModelFailureKind.Terminal);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task The_api_key_never_appears_anywhere_in_the_thrown_exceptions_ToString()
    {
        const string secretKeyText = "sk-ant-VERY-SECRET-DO-NOT-LEAK-abc123";
        var (categorizer, handler) = Build(key: secretKeyText);
        handler.Enqueue(HttpStatusCode.Unauthorized, AnthropicResponses.AuthenticationError);
        var request = Request("Coffee 3.50 EUR");

        var act = () => categorizer.ProposeAsync(request, TestContext.Current.CancellationToken);
        var assertion = await act.Should().ThrowAsync<ModelCallException>();

        assertion.Which.ToString().Should().NotContain(secretKeyText);
        (assertion.Which.InnerException?.ToString() ?? "").Should().NotContain(secretKeyText);
        handler.Requests[0].Headers.GetValueOrDefault("x-api-key", "").Should().Be(secretKeyText,
            "confirms the key really was sent as a header, so the ToString() assertions above are actually exercising the leak path, not skipping it");
    }

    [Fact]
    public async Task CanonicalizeMerchantAsync_sends_a_single_forced_strict_tool_call_and_returns_the_display_name()
    {
        var (categorizer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.CanonicalizeMerchantJsonAnswer);
        var known = new List<MerchantOption> { new(Guid.NewGuid(), "Lidl Beograd") };

        var displayName = await categorizer.CanonicalizeMerchantAsync("lidl", known, TestContext.Current.CancellationToken);

        displayName.Should().Be("Lidl");
        handler.Requests.Should().ContainSingle();
        var sent = JsonDocument.Parse(handler.Requests[0].Body).RootElement;
        sent.TryGetProperty("output_config", out _).Should().BeFalse("this is a tool call, not a structured-output answer");
        var tools = sent.GetProperty("tools");
        tools.GetArrayLength().Should().Be(1);
        tools[0].GetProperty("name").GetString().Should().Be("canonicalize_merchant");
        tools[0].GetProperty("strict").GetBoolean().Should().BeTrue();
        sent.GetProperty("tool_choice").GetProperty("type").GetString().Should().Be("tool");
        sent.GetProperty("tool_choice").GetProperty("name").GetString().Should().Be("canonicalize_merchant");
    }

    [Fact]
    public async Task A_proposal_asks_for_the_configured_output_budget_and_a_canonicalisation_for_a_small_one()
    {
        // The budget is the provider's configuration now (AnthropicOptions.MaxTokens, applied as the
        // adapter's default); the categoriser only overrides it where it wants less.
        var (categorizer, handler) = Build(maxTokens: 3072);
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.RecordSpendingJsonAnswer);
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.CanonicalizeMerchantJsonAnswer);

        await categorizer.ProposeAsync(Request("Coffee 3.50 EUR"), TestContext.Current.CancellationToken);
        await categorizer.CanonicalizeMerchantAsync("lidl", [], TestContext.Current.CancellationToken);

        JsonDocument.Parse(handler.Requests[0].Body).RootElement.GetProperty("max_tokens").GetInt32().Should().Be(3072);
        JsonDocument.Parse(handler.Requests[1].Body).RootElement.GetProperty("max_tokens").GetInt32().Should().Be(256);
    }
}
