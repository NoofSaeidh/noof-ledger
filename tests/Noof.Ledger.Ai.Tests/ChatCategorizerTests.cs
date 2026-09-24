using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Noof.Ledger.Application.Categorization;

namespace Noof.Ledger.Ai.Tests;

// The categoriser with no provider underneath it at all. What reaches the wire is pinned by
// ChatCategorizerOverAnthropicTests; this pins what the provider-neutral layer itself says.
public class ChatCategorizerTests
{
    static readonly CategorizationRequest Request = new(
        "кофе 250", new DateOnly(2026, 9, 22),
        [new CategoryOption("food-drink", "Food & Drink", "Еда и напитки", null)], [], []);

    [Fact]
    public async Task Every_tool_it_offers_is_strict_in_provider_neutral_terms_and_in_no_providers_own()
    {
        var provider = new ScriptedChatClient()
            .Answer(new FunctionCallContent("call_1", "record_transaction",
                new Dictionary<string, object?>
                {
                    ["items"] = Array.Empty<object>(), ["occurred_on"] = null, ["kind"] = "expense",
                    ["wallet_id"] = null, ["balance_amount"] = null, ["balance_currency"] = null,
                }))
            .Answer(new FunctionCallContent("call_2", "canonicalize_merchant",
                new Dictionary<string, object?> { ["display_name"] = "Lidl" }));
        var categorizer = new ChatCategorizer(new FixedChatClientFactory(provider));

        await categorizer.ProposeAsync(Request, TestContext.Current.CancellationToken);
        await categorizer.CanonicalizeMerchantAsync("lidl", [], TestContext.Current.CancellationToken);

        var offered = provider.Requests.SelectMany(request => request.Options!.Tools!).ToList();
        offered.Select(tool => tool.Name).Should().BeEquivalentTo(["list_merchants", "record_transaction", "canonicalize_merchant"]);
        offered.Should().OnlyContain(tool => tool.IsStrict());
        offered.Should().OnlyContain(tool => !tool.AdditionalProperties.ContainsKey("Strict"),
            "\"Strict\" is what one provider's adapter reads; saying it here would tie this layer to that provider");
    }

    sealed class FixedChatClientFactory(IChatClient client) : IChatClientFactory
    {
        public Task<IChatClient> CreateAsync(CancellationToken cancellationToken) => Task.FromResult(client);
    }
}
