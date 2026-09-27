using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Noof.Ledger.Ai.Anthropic;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Application.Wallets;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Noof.Ledger.Ai.Tests.Anthropic;

// ChatReceiptCategorizer end to end over the real Anthropic pipeline, asserted on the HTTP body the
// SDK actually sent - the same pattern ChatCategorizerOverAnthropicTests uses.
public class ChatReceiptCategorizerOverAnthropicTests
{
    static readonly IReadOnlyList<CategoryEntry> Categories =
    [
        new CategoryEntry(Guid.NewGuid(), "groceries", "Groceries", "Продукты", null),
        new CategoryEntry(Guid.NewGuid(), "other", "Other", "Прочее", null),
    ];

    static readonly IReadOnlyList<WalletOption> NoWallets = [];

    static (ChatReceiptCategorizer Categorizer, StubHttpMessageHandler Handler) Build(IReadOnlyList<WalletOption>? wallets = null)
    {
        var handler = new StubHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var secretStore = new StubSecretStore(SecretState.Present, "sk-ant-test-key-do-not-log-me");
        var options = new AnthropicOptions { Model = "claude-haiku-4-5-20251001", MaxTokens = 2048, Timeout = TimeSpan.FromSeconds(90) };
        var clientFactory = new AnthropicChatClientFactory(
            secretStore, httpClient, options, new OperationTimer(TimeProvider.System, new SlowOperationOptions()),
            NullLogger<AnthropicChatClientFactory>.Instance);

        var categoryCatalog = Substitute.For<ICategoryCatalog>();
        categoryCatalog.ActiveAsync(Arg.Any<CancellationToken>()).Returns(Categories);

        var walletDirectory = Substitute.For<IWalletDirectory>();
        walletDirectory.ActiveAsync(Arg.Any<CancellationToken>()).Returns(wallets ?? NoWallets);

        var categorizer = new ChatReceiptCategorizer(
            clientFactory, categoryCatalog, walletDirectory,
            new OperationTimer(TimeProvider.System, new SlowOperationOptions()), NullLogger<ChatReceiptCategorizer>.Instance);
        return (categorizer, handler);
    }

    static ReceiptCategorizationRequest Request() => new(
        [new ReceiptLineToCategorize(1, "Mleko", 1, 120), new ReceiptLineToCategorize(2, "Hleb", 2, 180.50m)],
        "Maxi", "123456789", MerchantKnown: false, Caption: null);

    [Fact]
    public async Task Sends_a_forced_strict_categorize_receipt_tool_with_the_catalogues_slugs()
    {
        var (categorizer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.CategorizeReceiptJsonAnswer);

        await categorizer.CategorizeAsync(Request(), TestContext.Current.CancellationToken);

        var sent = JsonDocument.Parse(handler.Requests[0].Body).RootElement;
        var tools = sent.GetProperty("tools");
        tools.GetArrayLength().Should().Be(1);
        tools[0].GetProperty("name").GetString().Should().Be("categorize_receipt");
        tools[0].GetProperty("strict").GetBoolean().Should().BeTrue();
        sent.GetProperty("tool_choice").GetProperty("type").GetString().Should().Be("tool");
        sent.GetProperty("tool_choice").GetProperty("name").GetString().Should().Be("categorize_receipt");

        var slugEnum = tools[0].GetProperty("input_schema").GetProperty("properties").GetProperty("lines")
            .GetProperty("items").GetProperty("properties").GetProperty("category_slug")
            .GetProperty("enum").EnumerateArray().Select(e => e.GetString());
        slugEnum.Should().BeEquivalentTo(["groceries", "other"]);

        handler.Requests[0].Body.Should().Contain("Maxi").And.Contain("123456789");
    }

    [Fact]
    public async Task Maps_the_answer_to_a_ReceiptCategorization()
    {
        var (categorizer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.CategorizeReceiptJsonAnswer);

        var result = await categorizer.CategorizeAsync(Request(), TestContext.Current.CancellationToken);

        result.Lines.Should().BeEquivalentTo(
        [
            new ReceiptLineCategory(1, "groceries"),
            new ReceiptLineCategory(2, "groceries"),
        ]);
        result.MerchantCanonicalName.Should().Be("Maxi");
        result.WalletId.Should().BeNull();
    }

    [Fact]
    public async Task A_correction_reaches_the_wire_and_a_declined_amount_change_is_mapped_back()
    {
        var (categorizer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, """
            {"id":"msg_11","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
             "content":[{"type":"tool_use","id":"toolu_11","name":"categorize_receipt","input":{"lines":[{"ordinal":1,"category_slug":"groceries"},{"ordinal":2,"category_slug":"groceries"}],"merchant_name":"Maxi","wallet_id":null,"unsupported_change":"amount"}}],
             "stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":150,"output_tokens":20}}
            """);

        var request = Request() with { Correction = "actually it was 200, not 180.50" };
        var result = await categorizer.CategorizeAsync(request, TestContext.Current.CancellationToken);

        result.UnsupportedChange.Should().Be(UnsupportedChangeKind.Amount);
        handler.Requests[0].Body.Should().Contain("actually it was 200, not 180.50");

        var schema = JsonDocument.Parse(handler.Requests[0].Body).RootElement
            .GetProperty("tools")[0].GetProperty("input_schema");
        schema.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .Should().Contain("unsupported_change");
    }

    [Fact]
    public async Task A_declined_date_change_is_mapped_back_too()
    {
        var (categorizer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, """
            {"id":"msg_12","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
             "content":[{"type":"tool_use","id":"toolu_12","name":"categorize_receipt","input":{"lines":[{"ordinal":1,"category_slug":"groceries"},{"ordinal":2,"category_slug":"groceries"}],"merchant_name":"Maxi","wallet_id":null,"unsupported_change":"date"}}],
             "stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":150,"output_tokens":20}}
            """);

        var request = Request() with { Correction = "that was yesterday" };
        var result = await categorizer.CategorizeAsync(request, TestContext.Current.CancellationToken);

        result.UnsupportedChange.Should().Be(UnsupportedChangeKind.Date);
    }

    [Fact]
    public async Task A_named_wallet_is_offered_and_returned()
    {
        var walletId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var wallets = new List<WalletOption> { new(walletId, "Cash", Noof.Ledger.Domain.CurrencyCode.Rsd, [], true) };
        var (categorizer, handler) = Build(wallets);
        handler.Enqueue(HttpStatusCode.OK, $$$"""
            {"id":"msg_10","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
             "content":[{"type":"tool_use","id":"toolu_10","name":"categorize_receipt","input":{"lines":[{"ordinal":1,"category_slug":"groceries"},{"ordinal":2,"category_slug":"groceries"}],"merchant_name":null,"wallet_id":"{{{walletId}}}"}}],
             "stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":150,"output_tokens":20}}
            """);

        var result = await categorizer.CategorizeAsync(Request(), TestContext.Current.CancellationToken);

        result.WalletId.Should().Be(walletId);

        var sent = JsonDocument.Parse(handler.Requests[0].Body).RootElement;
        var walletIdSchema = sent.GetProperty("tools")[0].GetProperty("input_schema").GetProperty("properties")
            .GetProperty("wallet_id");
        walletIdSchema.TryGetProperty("type", out _).Should().BeFalse(
            "a nullable enum must reach the wire as an anyOf, never an enum beside a type array");
        var branches = walletIdSchema.GetProperty("anyOf").EnumerateArray().ToList();
        branches[0].GetProperty("enum").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo([walletId.ToString()]);
        branches[1].GetProperty("type").GetString().Should().Be("null");
    }
}
