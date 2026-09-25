using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;

namespace Noof.Ledger.Ai.Tests;

public class ReceiptVisionSchemaTests
{
    [Fact]
    public void Payment_method_is_a_nullable_enum_expressed_as_anyOf_not_a_type_array()
    {
        var schema = ReceiptVisionSchema.BuildReadReceipt();

        var paymentMethod = schema.GetProperty("properties").GetProperty("payment_method");

        paymentMethod.TryGetProperty("type", out _).Should().BeFalse(
            "a type array beside an enum containing null is exactly what Anthropic rejected in record_transaction (4161444)");
        var branches = paymentMethod.GetProperty("anyOf").EnumerateArray().ToList();
        branches.Should().HaveCount(2);
        branches[0].GetProperty("type").GetString().Should().Be("string");
        branches[0].GetProperty("enum").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["card", "cash", "transfer", "voucher", "other", "mixed"]);
        branches[1].GetProperty("type").GetString().Should().Be("null");
    }

    // The API rejects an enum next to a type array - "Enum value 'EUR' does not match declared type
    // '['string', 'null']'" (live 400, 2026-09-25) - so every nullable enum is an anyOf instead.
    [Fact]
    public void No_node_declares_an_enum_alongside_a_type_array()
    {
        var schema = Reserialize(ReceiptVisionSchema.BuildReadReceipt());

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
