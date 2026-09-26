using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.TestKit;

namespace Noof.Ledger.Ai.Tests;

// The categoriser with no provider underneath it at all. What reaches the wire is pinned by
// ChatCategorizerOverAnthropicTests; this pins what the provider-neutral layer itself says.
public class ChatCategorizerTests
{
    static readonly CategorizationRequest Request = new(
        "кофе 250", new DateOnly(2026, 9, 22),
        [new CategoryOption("food-drink", "Food & Drink", "Еда и напитки", null)], [], []);

    static FunctionCallContent RecordTransactionCall(string callId) =>
        new(callId, "record_transaction", new Dictionary<string, object?>
        {
            ["items"] = Array.Empty<object>(), ["occurred_on"] = null, ["kind"] = "expense",
            ["wallet_id"] = null, ["balance_amount"] = null, ["balance_currency"] = null,
        });

    static FunctionCallContent ListMerchantsCall(string callId) =>
        new(callId, "list_merchants", new Dictionary<string, object?>());

    [Fact]
    public async Task Every_tool_it_offers_is_strict_in_provider_neutral_terms_and_in_no_providers_own()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var timer = new OperationTimer(clock, new SlowOperationOptions());
        var logger = new CapturingLogger<ChatCategorizer>();
        var provider = new ScriptedChatClient()
            .Answer(RecordTransactionCall("call_1"))
            .Answer(new FunctionCallContent("call_2", "canonicalize_merchant",
                new Dictionary<string, object?> { ["display_name"] = "Lidl" }));
        var categorizer = new ChatCategorizer(new FixedChatClientFactory(provider), timer, logger);

        await categorizer.ProposeAsync(Request, TestContext.Current.CancellationToken);
        await categorizer.CanonicalizeMerchantAsync("lidl", [], TestContext.Current.CancellationToken);

        var offered = provider.Requests.SelectMany(request => request.Options!.Tools!).ToList();
        offered.Select(tool => tool.Name).Should().BeEquivalentTo(["list_merchants", "record_transaction", "canonicalize_merchant"]);
        offered.Should().OnlyContain(tool => tool.IsStrict());
        offered.Should().OnlyContain(tool => !tool.AdditionalProperties.ContainsKey("Strict"),
            "\"Strict\" is what one provider's adapter reads; saying it here would tie this layer to that provider");
    }

    [Fact]
    public async Task A_scripted_client_that_advances_the_clock_gives_one_model_categorize_timing()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var timer = new OperationTimer(clock, new SlowOperationOptions());
        var logger = new CapturingLogger<ChatCategorizer>();
        var provider = new ScriptedChatClient().AnswerAfterDelay(clock, TimeSpan.FromMilliseconds(50), RecordTransactionCall("call_1"));
        var categorizer = new ChatCategorizer(new FixedChatClientFactory(provider), timer, logger);

        await categorizer.ProposeAsync(Request, TestContext.Current.CancellationToken);

        logger.Entries.Should().ContainSingle(entry => (string)entry.Properties["Operation"] == "model.categorize");
    }

    [Fact]
    public async Task The_list_merchants_path_gives_two_model_request_timings_and_one_model_categorize()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var timer = new OperationTimer(clock, new SlowOperationOptions());
        var logger = new CapturingLogger<ChatCategorizer>();
        var provider = new ScriptedChatClient()
            .AnswerAfterDelay(clock, TimeSpan.FromMilliseconds(10), ListMerchantsCall("call_1"))
            .AnswerAfterDelay(clock, TimeSpan.FromMilliseconds(10), RecordTransactionCall("call_2"));
        var categorizer = new ChatCategorizer(new FixedChatClientFactory(provider), timer, logger);

        await categorizer.ProposeAsync(Request, TestContext.Current.CancellationToken);

        logger.Entries.Count(entry => (string)entry.Properties["Operation"] == "model.request").Should().Be(2);
        logger.Entries.Should().ContainSingle(entry => (string)entry.Properties["Operation"] == "model.categorize");
    }

    [Fact]
    public async Task CanonicalizeMerchantAsync_gives_model_canonicalize()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var timer = new OperationTimer(clock, new SlowOperationOptions());
        var logger = new CapturingLogger<ChatCategorizer>();
        var provider = new ScriptedChatClient().AnswerAfterDelay(clock, TimeSpan.FromMilliseconds(5),
            new FunctionCallContent("call_1", "canonicalize_merchant", new Dictionary<string, object?> { ["display_name"] = "Lidl" }));
        var categorizer = new ChatCategorizer(new FixedChatClientFactory(provider), timer, logger);

        await categorizer.CanonicalizeMerchantAsync("lidl", [], TestContext.Current.CancellationToken);

        logger.Entries.Should().ContainSingle(entry => (string)entry.Properties["Operation"] == "model.canonicalize");
    }

    [Fact]
    public async Task A_ModelCallException_still_logs_the_categorize_duration()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var timer = new OperationTimer(clock, new SlowOperationOptions());
        var logger = new CapturingLogger<ChatCategorizer>();
        var provider = new ScriptedChatClient().AnswerAfterDelay(clock, TimeSpan.FromMilliseconds(5), new TextContent("не знаю"));
        var categorizer = new ChatCategorizer(new FixedChatClientFactory(provider), timer, logger);

        var act = () => categorizer.ProposeAsync(Request, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ModelCallException>();
        logger.Entries.Should().Contain(entry => (string)entry.Properties["Operation"] == "model.categorize");
    }

    sealed class FixedChatClientFactory(IChatClient client) : IChatClientFactory
    {
        public Task<IChatClient> CreateAsync(CancellationToken cancellationToken) => Task.FromResult(client);
    }
}
