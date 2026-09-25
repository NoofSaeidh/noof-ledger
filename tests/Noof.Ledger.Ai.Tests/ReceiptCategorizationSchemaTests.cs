using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Ai.Tests;

public class ReceiptCategorizationSchemaTests
{
    static readonly IReadOnlyList<CategoryOption> Categories =
        [new CategoryOption("groceries", "Groceries", "Продукты", null)];

    static readonly IReadOnlyList<WalletOption> NoWallets = [];

    static readonly IReadOnlyList<WalletOption> OneWallet =
    [
        new WalletOption(Guid.Parse("33333333-3333-3333-3333-333333333333"), "Cash", CurrencyCode.Rsd, [], true),
    ];

    [Fact]
    public void Wallet_id_enum_is_expressed_as_anyOf_not_a_type_array_when_wallets_are_offered()
    {
        var schema = ReceiptCategorizationSchema.BuildCategorizeReceipt(Categories, OneWallet);

        var walletId = schema.GetProperty("properties").GetProperty("wallet_id");

        walletId.TryGetProperty("type", out _).Should().BeFalse(
            "a type array beside an enum containing null is exactly what Anthropic rejected in record_transaction (4161444)");
        var branches = walletId.GetProperty("anyOf").EnumerateArray().ToList();
        branches.Should().HaveCount(2);
        branches[0].GetProperty("type").GetString().Should().Be("string");
        branches[0].GetProperty("enum").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["33333333-3333-3333-3333-333333333333"]);
        branches[1].GetProperty("type").GetString().Should().Be("null");
    }

    [Fact]
    public void Wallet_id_can_only_be_null_when_no_wallets_are_offered()
    {
        var schema = ReceiptCategorizationSchema.BuildCategorizeReceipt(Categories, NoWallets);

        schema.GetProperty("properties").GetProperty("wallet_id").GetProperty("type").GetString().Should().Be("null");
    }

    // The API rejects an enum next to a type array - "Enum value 'EUR' does not match declared type
    // '['string', 'null']'" (live 400, 2026-09-25) - so every nullable enum is an anyOf instead.
    [Fact]
    public void No_node_declares_an_enum_alongside_a_type_array()
    {
        var schema = Reserialize(ReceiptCategorizationSchema.BuildCategorizeReceipt(Categories, OneWallet));

        Descendants(schema).OfType<JsonObject>()
            .Where(node => node["enum"] is not null && node["type"] is JsonArray)
            .Should().BeEmpty();
    }

    static IEnumerable<JsonNode> Descendants(JsonNode node) => node switch
    {
        JsonObject obj => [obj, .. obj.Where(p => p.Value is not null).SelectMany(p => Descendants(p.Value!))],
        JsonArray array => [array, .. array.Where(item => item is not null).SelectMany(item => Descendants(item!))],
        _ => [node],
    };

    static JsonNode Reserialize(JsonElement schema) => JsonNode.Parse(schema.GetRawText())!;
}
