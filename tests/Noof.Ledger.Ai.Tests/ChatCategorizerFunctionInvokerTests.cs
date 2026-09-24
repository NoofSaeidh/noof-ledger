using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Noof.Ledger.Application.Categorization;

namespace Noof.Ledger.Ai.Tests;

// The tool-calling pipeline itself - FunctionInvokingChatClient over AnswerToolGuard, both
// provider-neutral - proven against a mocked IChatClient with no HTTP and no SDK underneath.
// ChatCategorizerOverAnthropicTests pins what reaches the wire; this pins what
// Microsoft.Extensions.AI actually does with the request before any wire exists.
public class ChatCategorizerFunctionInvokerTests
{
    static readonly MerchantOption Lidl = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Lidl");
    static readonly MerchantOption Maxi = new(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Maxi");

    static CategorizationRequest Request(IReadOnlyList<MerchantOption>? hints = null, IReadOnlyList<MerchantOption>? all = null) =>
        new("кофе 250", new DateOnly(2026, 9, 22),
            [new CategoryOption("food-drink", "Food & Drink", "Еда и напитки", null)],
            hints ?? [], all ?? [Lidl, Maxi]);

    static FunctionCallContent RecordTransactionCall(string callId, decimal amount) =>
        new(callId, "record_transaction", new Dictionary<string, object?>
        {
            ["items"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["description"] = "кофе", ["amount"] = amount, ["currency"] = null,
                    ["category_slug"] = "food-drink", ["merchant_name"] = null,
                },
            },
            ["occurred_on"] = null,
            ["kind"] = "expense",
            ["wallet_id"] = null,
            ["balance_amount"] = null,
            ["balance_currency"] = null,
        });

    static FunctionCallContent ListMerchantsCall(string callId) =>
        new(callId, "list_merchants", new Dictionary<string, object?>());

    static ChatCategorizer Build(ScriptedChatClient provider) => new(new FixedChatClientFactory(provider));

    [Fact]
    public async Task A_direct_record_transaction_answer_on_the_first_call_ends_the_loop_in_one_call()
    {
        var provider = new ScriptedChatClient().Answer(RecordTransactionCall("call_1", 12345678901234567.89m));
        var categorizer = Build(provider);

        var proposal = await categorizer.ProposeAsync(Request(), TestContext.Current.CancellationToken);

        provider.Requests.Should().ContainSingle("a direct answer must not trigger a follow-up call");
        proposal.Items.Should().ContainSingle().Which.Amount.Should().Be(12345678901234567.89m,
            "amount is read as a JSON number straight into decimal, never through double");

        var call1 = provider.Requests[0].Options!;
        call1.Tools!.Select(t => t.Name).Should().BeEquivalentTo(["list_merchants", "record_transaction"]);
        call1.Tools!.Should().OnlyContain(tool => tool.IsStrict(), "every tool this layer offers is strict");
        call1.ToolMode.Should().Be(ChatToolMode.RequireAny, "the model must answer one of the two, never plain text");
    }

    [Fact]
    public async Task A_tiny_amount_also_round_trips_into_decimal_exactly()
    {
        var provider = new ScriptedChatClient().Answer(RecordTransactionCall("call_1", 0.1m));
        var categorizer = Build(provider);

        var proposal = await categorizer.ProposeAsync(Request(), TestContext.Current.CancellationToken);

        proposal.Items.Should().ContainSingle().Which.Amount.Should().Be(0.1m);
    }

    [Fact]
    public async Task Calling_list_merchants_first_is_answered_locally_and_the_follow_up_offers_only_record_transaction_forced()
    {
        var provider = new ScriptedChatClient()
            .Answer(ListMerchantsCall("call_1"))
            .Answer(RecordTransactionCall("call_2", 3.5m));
        var categorizer = Build(provider);

        var proposal = await categorizer.ProposeAsync(Request([Lidl], [Lidl, Maxi]), TestContext.Current.CancellationToken);

        provider.Requests.Should().HaveCount(2, "the local lookup plus one follow-up, never a third");
        proposal.Items.Should().ContainSingle();

        var call2 = provider.Requests[1];
        call2.Options!.Tools!.Select(t => t.Name).Should().BeEquivalentTo(["record_transaction"],
            "record_transaction is structurally the only tool left to call, ruling out a second lookup");
        call2.Options.Tools!.Should().OnlyContain(tool => tool.IsStrict());
        call2.Options.ToolMode.Should().Be(ChatToolMode.RequireSpecific("record_transaction"));

        var results = call2.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToList();
        var result = results.Should().ContainSingle().Which;
        result.CallId.Should().Be("call_1");
        result.Result.Should().Be(JsonSerializer.Serialize(new[] { Lidl, Maxi }),
            "the local answer is the FULL directory (AllMerchants), not the short hint list offered on call 1");
    }

    [Fact]
    public async Task Asking_for_the_merchant_list_a_second_time_gets_no_third_call_and_fails_transiently()
    {
        var provider = new ScriptedChatClient()
            .Answer(ListMerchantsCall("call_1"))
            .Answer(ListMerchantsCall("call_2"));
        var categorizer = Build(provider);

        var act = () => categorizer.ProposeAsync(Request(), TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<ModelCallException>();
        assertion.Which.Kind.Should().Be(ModelFailureKind.Transient);
        provider.Requests.Should().HaveCount(2,
            "a third call would mean the iteration limit let a second lookup through; the pipeline must not humour a model that ignores the forced tool mode");
    }

    [Fact]
    public async Task Plain_text_with_no_tool_call_on_the_first_turn_fails_after_exactly_one_call()
    {
        var provider = new ScriptedChatClient().Answer(new TextContent("не знаю, о чём вы"));
        var categorizer = Build(provider);

        var act = () => categorizer.ProposeAsync(Request(), TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<ModelCallException>();
        assertion.Which.Kind.Should().Be(ModelFailureKind.Transient);
        provider.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Plain_text_with_no_tool_call_on_the_follow_up_fails_after_exactly_two_calls()
    {
        var provider = new ScriptedChatClient()
            .Answer(ListMerchantsCall("call_1"))
            .Answer(new TextContent("всё ещё не знаю"));
        var categorizer = Build(provider);

        var act = () => categorizer.ProposeAsync(Request(), TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<ModelCallException>();
        assertion.Which.Kind.Should().Be(ModelFailureKind.Transient);
        provider.Requests.Should().HaveCount(2, "a text-only follow-up gets no third call either");
    }

    [Fact]
    public async Task Calling_both_tools_at_once_on_the_first_turn_is_read_as_record_transaction_with_no_follow_up()
    {
        var provider = new ScriptedChatClient().Answer(ListMerchantsCall("call_1a"), RecordTransactionCall("call_1b", 3.5m));
        var categorizer = Build(provider);

        var proposal = await categorizer.ProposeAsync(Request(), TestContext.Current.CancellationToken);

        provider.Requests.Should().ContainSingle("record_transaction answers the request outright even when list_merchants was also called");
        proposal.Items.Should().ContainSingle().Which.Amount.Should().Be(3.5m);
    }

    [Fact]
    public async Task Canonicalisation_is_a_single_forced_strict_tool_call()
    {
        var provider = new ScriptedChatClient().Answer(
            new FunctionCallContent("call_1", "canonicalize_merchant", new Dictionary<string, object?> { ["display_name"] = "Lidl" }));
        var categorizer = Build(provider);

        var displayName = await categorizer.CanonicalizeMerchantAsync("lidl", [Lidl], TestContext.Current.CancellationToken);

        displayName.Should().Be("Lidl");
        provider.Requests.Should().ContainSingle();
        var options = provider.Requests[0].Options!;
        options.Tools!.Select(tool => tool.Name).Should().BeEquivalentTo(["canonicalize_merchant"]);
        options.Tools![0].IsStrict().Should().BeTrue();
        options.ToolMode.Should().Be(ChatToolMode.RequireSpecific("canonicalize_merchant"));
    }

    [Fact]
    public async Task An_exception_from_the_inner_client_propagates_unwrapped_and_is_never_retried()
    {
        var failure = new InvalidOperationException("the mock provider is unavailable");
        var provider = new ScriptedChatClient().Fail(failure);
        var categorizer = Build(provider);

        var act = () => categorizer.ProposeAsync(Request(), TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.Which.Should().BeSameAs(failure, "ChatCategorizer wraps nothing - translation is the provider layer's job");
        provider.Requests.Should().ContainSingle("FunctionInvokingChatClient must not retry a failed call");
    }

    sealed class FixedChatClientFactory(IChatClient client) : IChatClientFactory
    {
        public Task<IChatClient> CreateAsync(CancellationToken cancellationToken) => Task.FromResult(client);
    }
}
