using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Ai.Tests;

public class AnthropicCategorizerTests
{
    static readonly IReadOnlyList<CategoryOption> Categories =
        [new CategoryOption("food-drink", "Food & Drink", "Еда и напитки", null)];

    static readonly IReadOnlyList<MerchantOption> NoMerchantHints = [];

    static (AnthropicCategorizer Categorizer, StubHttpMessageHandler Handler) Build(
        SecretState state = SecretState.Present, string? key = "sk-ant-test-key-do-not-log-me")
    {
        var handler = new StubHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var secretStore = new StubSecretStore(state, key);
        var options = new AnthropicOptions { Model = "claude-haiku-4-5-20251001", MaxTokens = 2048, Timeout = TimeSpan.FromSeconds(90) };
        var clientFactory = new AnthropicClientFactory(secretStore, httpClient, options);
        return (new AnthropicCategorizer(clientFactory, options), handler);
    }

    [Fact]
    public async Task Returns_the_proposal_when_the_first_turn_answers_with_JSON_text()
    {
        var (categorizer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.RecordSpendingJsonAnswer);
        var request = new CategorizationRequest("Coffee 3.50 EUR", Categories, NoMerchantHints, NoMerchantHints);

        var proposal = await categorizer.ProposeAsync(request, TestContext.Current.CancellationToken);

        proposal.Items.Should().ContainSingle();
        proposal.Items[0].Description.Should().Be("Coffee");
        proposal.Items[0].AmountQuote.Should().Be("3.50");
        proposal.Items[0].CurrencyCode.Should().Be("EUR", "the JSON field is \"currency\", not \"currency_code\" — this is the mapping Locked Decision 5 exists for");
        proposal.Items[0].CategorySlug.Should().Be("food-drink");
        handler.Requests.Should().ContainSingle("a direct JSON answer on the first turn must not trigger a follow-up call");
    }

    [Fact]
    public async Task Sends_json_schema_output_config_and_exactly_one_tool_on_the_first_call()
    {
        var (categorizer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.RecordSpendingJsonAnswer);
        var request = new CategorizationRequest("Coffee 3.50 EUR", Categories, NoMerchantHints, NoMerchantHints);

        await categorizer.ProposeAsync(request, TestContext.Current.CancellationToken);

        var sent = JsonDocument.Parse(handler.Requests[0].Body).RootElement;
        sent.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString().Should().Be("json_schema");
        var currencyEnum = sent.GetProperty("output_config").GetProperty("format").GetProperty("schema")
            .GetProperty("properties").GetProperty("items").GetProperty("items").GetProperty("properties")
            .GetProperty("currency").GetProperty("enum").EnumerateArray().Select(e => e.GetString());
        currencyEnum.Should().BeEquivalentTo(["EUR", "RSD", "USD", "RUB", "KZT"]);

        var tools = sent.GetProperty("tools");
        tools.GetArrayLength().Should().Be(1, "guarding against the RawRepresentationFactory trap: a tool must never appear twice");
        tools[0].GetProperty("name").GetString().Should().Be("list_merchants");
        sent.GetProperty("tool_choice").GetProperty("type").GetString().Should().Be("auto");

        sent.TryGetProperty("temperature", out _).Should().BeFalse("ChatOptions.Temperature must never be set");
    }

    [Fact]
    public async Task No_request_carries_an_anthropic_beta_header()
    {
        var (categorizer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.RecordSpendingJsonAnswer);
        var request = new CategorizationRequest("Coffee 3.50 EUR", Categories, NoMerchantHints, NoMerchantHints);

        await categorizer.ProposeAsync(request, TestContext.Current.CancellationToken);

        handler.Requests[0].Headers.Should().NotContainKey("anthropic-beta",
            "structured outputs and tool use are GA — no beta header anywhere");
    }

    [Fact]
    public async Task Answers_list_merchants_then_sends_one_follow_up_that_offers_no_tools()
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
        var request = new CategorizationRequest("Lidl 3.50 EUR", Categories, hints, all);

        var proposal = await categorizer.ProposeAsync(request, TestContext.Current.CancellationToken);

        proposal.Items.Should().ContainSingle();
        handler.Requests.Should().HaveCount(2, "the loop runs exactly once: one first call, one follow-up, never a third");

        var secondSent = JsonDocument.Parse(handler.Requests[1].Body).RootElement;
        secondSent.TryGetProperty("tools", out _).Should().BeFalse(
            "the follow-up call offers no tools at all, which is what structurally rules out a third list_merchants call");

        // The answer is the FULL directory. "Maxi" is deliberately absent from the hints, so a
        // regression that answers with request.MerchantHints instead of request.AllMerchants makes
        // this line fail - and nothing else in the suite would have noticed.
        handler.Requests[1].Body.Should().Contain("Maxi");
    }

    [Fact]
    public async Task A_model_that_asks_for_the_merchant_list_twice_never_gets_a_third_request()
    {
        var (categorizer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.ListMerchantsToolUse);
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.ListMerchantsToolUse);
        var request = new CategorizationRequest("Lidl 3.50 EUR", Categories, NoMerchantHints, NoMerchantHints);

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
        var request = new CategorizationRequest("what is this", Categories, NoMerchantHints, NoMerchantHints);

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
        var request = new CategorizationRequest("Coffee 3.50 EUR", Categories, NoMerchantHints, NoMerchantHints);

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
        var request = new CategorizationRequest("Coffee 3.50 EUR", Categories, NoMerchantHints, NoMerchantHints);

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
        var request = new CategorizationRequest("Coffee 3.50 EUR", Categories, NoMerchantHints, NoMerchantHints);

        var act = () => categorizer.ProposeAsync(request, TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<ModelCallException>();
        assertion.Which.Kind.Should().Be(ModelFailureKind.Terminal);
        assertion.Which.IsAccountLevel().Should().BeFalse();
    }

    [Fact]
    public async Task A_missing_key_is_a_terminal_failure_without_ever_calling_the_network()
    {
        var (categorizer, handler) = Build(state: SecretState.Missing, key: null);
        var request = new CategorizationRequest("Coffee 3.50 EUR", Categories, NoMerchantHints, NoMerchantHints);

        var act = () => categorizer.ProposeAsync(request, TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<ModelCallException>();
        assertion.Which.Kind.Should().Be(ModelFailureKind.Terminal);
        handler.Requests.Should().BeEmpty("a missing key must fail before any HTTP call is attempted");
    }

    [Fact]
    public async Task An_unreadable_key_is_a_terminal_failure_without_ever_calling_the_network()
    {
        var (categorizer, handler) = Build(state: SecretState.Unreadable, key: null);
        var request = new CategorizationRequest("Coffee 3.50 EUR", Categories, NoMerchantHints, NoMerchantHints);

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
        var request = new CategorizationRequest("Coffee 3.50 EUR", Categories, NoMerchantHints, NoMerchantHints);

        var act = () => categorizer.ProposeAsync(request, TestContext.Current.CancellationToken);
        var assertion = await act.Should().ThrowAsync<ModelCallException>();

        assertion.Which.ToString().Should().NotContain(secretKeyText);
        (assertion.Which.InnerException?.ToString() ?? "").Should().NotContain(secretKeyText);
        handler.Requests[0].Headers.GetValueOrDefault("x-api-key", "").Should().Be(secretKeyText,
            "confirms the key really was sent as a header, so the ToString() assertions above are actually exercising the leak path, not skipping it");
    }

    [Fact]
    public async Task CanonicalizeMerchantAsync_sends_a_single_call_with_json_schema_output_and_returns_the_display_name()
    {
        var (categorizer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.CanonicalizeMerchantJsonAnswer);
        var known = new List<MerchantOption> { new(Guid.NewGuid(), "Lidl Beograd") };

        var displayName = await categorizer.CanonicalizeMerchantAsync("lidl", known, TestContext.Current.CancellationToken);

        displayName.Should().Be("Lidl");
        handler.Requests.Should().ContainSingle();
        var sent = JsonDocument.Parse(handler.Requests[0].Body).RootElement;
        sent.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString().Should().Be("json_schema");
        sent.TryGetProperty("tools", out _).Should().BeFalse("canonicalisation never offers a tool - it is a plain structured-output call");
    }

    sealed class StubSecretStore(SecretState state, string? value) : ISecretStore
    {
        public Task<SecretResult> GetAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(new SecretResult(state, value));

        public Task<SecretStatus> GetStatusAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException("not exercised by this test double");

        public Task SetAsync(string key, string plaintext, CancellationToken cancellationToken) =>
            throw new NotSupportedException("not exercised by this test double");

        public Task<bool> TrySetIfMissingAsync(string key, string plaintext, CancellationToken cancellationToken) =>
            throw new NotSupportedException("not exercised by this test double");
    }
}
