using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Application.Wallets;
using Noof.Ledger.TestKit;
using NSubstitute;

namespace Noof.Ledger.Ai.Tests;

// The receipt categoriser with no provider underneath it at all, pinning ordinal reconciliation:
// what reaches the wire is pinned by ChatReceiptCategorizerOverAnthropicTests.
public class ChatReceiptCategorizerTests
{
    static readonly IOperationTimer NoopTimer = new OperationTimer(TimeProvider.System, new SlowOperationOptions());

    static readonly IReadOnlyList<CategoryEntry> Categories =
        [new CategoryEntry(Guid.NewGuid(), "groceries", "Groceries", "Продукты", null),
         new CategoryEntry(Guid.NewGuid(), "other", "Other", "Прочее", null)];

    static ChatReceiptCategorizer Build(IChatClient provider, IOperationTimer? timer = null, Microsoft.Extensions.Logging.ILogger<ChatReceiptCategorizer>? logger = null) =>
        new(new FixedChatClientFactory(provider), CatalogOf(Categories), Wallets([]), timer ?? NoopTimer, logger ?? NullLogger<ChatReceiptCategorizer>.Instance);

    static ICategoryCatalog CatalogOf(IReadOnlyList<CategoryEntry> categories)
    {
        var catalog = Substitute.For<ICategoryCatalog>();
        catalog.ActiveAsync(Arg.Any<CancellationToken>()).Returns(categories);
        return catalog;
    }

    static IWalletDirectory Wallets(IReadOnlyList<WalletOption> wallets)
    {
        var directory = Substitute.For<IWalletDirectory>();
        directory.ActiveAsync(Arg.Any<CancellationToken>()).Returns(wallets);
        return directory;
    }

    static ReceiptCategorizationRequest RequestWithOrdinals(params int[] ordinals) => new(
        [.. ordinals.Select(ordinal => new ReceiptLineToCategorize(ordinal, $"Item {ordinal}", 1, 100))],
        "Maxi", "123456789", MerchantKnown: false, Caption: null);

    static FunctionCallContent Answer(params (int Ordinal, string Slug)[] lines) => new(
        "call_1", "categorize_receipt",
        new Dictionary<string, object?>
        {
            ["lines"] = lines.Select(l => new Dictionary<string, object?> { ["ordinal"] = l.Ordinal, ["category_slug"] = l.Slug }).ToArray(),
            ["merchant_name"] = "Maxi",
            ["wallet_id"] = null,
        });

    [Fact]
    public async Task An_ordinal_the_model_never_answers_gets_the_catalogues_fallback_slug()
    {
        var provider = new ScriptedChatClient().Answer(Answer((1, "groceries")));
        var categorizer = Build(provider);

        var result = await categorizer.CategorizeAsync(RequestWithOrdinals(1, 2), TestContext.Current.CancellationToken);

        result.Lines.Should().BeEquivalentTo(
        [
            new ReceiptLineCategory(1, "groceries"),
            new ReceiptLineCategory(2, "other"),
        ]);
    }

    [Fact]
    public async Task A_duplicate_ordinal_in_the_answer_keeps_only_the_first()
    {
        var provider = new ScriptedChatClient().Answer(Answer((1, "groceries"), (1, "other")));
        var categorizer = Build(provider);

        var result = await categorizer.CategorizeAsync(RequestWithOrdinals(1), TestContext.Current.CancellationToken);

        result.Lines.Should().ContainSingle().Which.Should().Be(new ReceiptLineCategory(1, "groceries"));
    }

    [Fact]
    public async Task An_extra_ordinal_the_request_never_offered_is_ignored()
    {
        var provider = new ScriptedChatClient().Answer(Answer((1, "groceries"), (99, "other")));
        var categorizer = Build(provider);

        var result = await categorizer.CategorizeAsync(RequestWithOrdinals(1), TestContext.Current.CancellationToken);

        result.Lines.Should().ContainSingle().Which.Should().Be(new ReceiptLineCategory(1, "groceries"));
    }

    [Fact]
    public async Task The_tool_it_offers_is_strict_in_provider_neutral_terms()
    {
        var provider = new ScriptedChatClient().Answer(Answer((1, "groceries")));
        var categorizer = Build(provider);

        await categorizer.CategorizeAsync(RequestWithOrdinals(1), TestContext.Current.CancellationToken);

        var offered = provider.Requests.SelectMany(request => request.Options!.Tools!).ToList();
        offered.Should().ContainSingle().Which.IsStrict().Should().BeTrue();
    }

    [Fact]
    public async Task A_scripted_client_that_advances_the_clock_gives_one_model_categorizeReceipt_timing()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var timer = new OperationTimer(clock, new SlowOperationOptions());
        var logger = new CapturingLogger<ChatReceiptCategorizer>();
        var provider = new ScriptedChatClient().AnswerAfterDelay(clock, TimeSpan.FromMilliseconds(10), Answer((1, "groceries")));
        var categorizer = Build(provider, timer, logger);

        await categorizer.CategorizeAsync(RequestWithOrdinals(1), TestContext.Current.CancellationToken);

        logger.Entries.Should().ContainSingle(entry => (string)entry.Properties["Operation"] == "model.categorizeReceipt");
    }

    sealed class FixedChatClientFactory(IChatClient client) : IChatClientFactory
    {
        public Task<IChatClient> CreateAsync(CancellationToken cancellationToken) => Task.FromResult(client);
    }
}
