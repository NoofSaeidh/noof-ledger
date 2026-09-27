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

    // Copilot finding, PR #3: the prompt tells read_receipt to leave every field but
    // readable/unreadable_reason null when the photo is unreadable, but currency and kind were a plain
    // required enum with no null branch - an unreadable answer had to invent a currency and a sale/
    // refund it never read. Nullable via anyOf, still required (strict mode requires the property to be
    // present, not non-null).
    [Fact]
    public void Currency_is_a_nullable_enum_expressed_as_anyOf_not_a_type_array()
    {
        var schema = ReceiptVisionSchema.BuildReadReceipt();

        var currency = schema.GetProperty("properties").GetProperty("currency");

        currency.TryGetProperty("type", out _).Should().BeFalse(
            "a type array beside an enum containing null is exactly what Anthropic rejected in record_transaction (4161444)");
        var branches = currency.GetProperty("anyOf").EnumerateArray().ToList();
        branches.Should().HaveCount(2);
        branches[0].GetProperty("type").GetString().Should().Be("string");
        branches[0].GetProperty("enum").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["EUR", "RSD", "USD", "RUB", "KZT"]);
        branches[1].GetProperty("type").GetString().Should().Be("null");

        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        required.Should().Contain("currency");
    }

    [Fact]
    public void Kind_is_a_nullable_enum_expressed_as_anyOf_not_a_type_array()
    {
        var schema = ReceiptVisionSchema.BuildReadReceipt();

        var kind = schema.GetProperty("properties").GetProperty("kind");

        kind.TryGetProperty("type", out _).Should().BeFalse(
            "a type array beside an enum containing null is exactly what Anthropic rejected in record_transaction (4161444)");
        var branches = kind.GetProperty("anyOf").EnumerateArray().ToList();
        branches.Should().HaveCount(2);
        branches[0].GetProperty("type").GetString().Should().Be("string");
        branches[0].GetProperty("enum").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["sale", "refund"]);
        branches[1].GetProperty("type").GetString().Should().Be("null");

        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        required.Should().Contain("kind");
    }

    [Fact]
    public void Readable_is_a_required_boolean()
    {
        var schema = ReceiptVisionSchema.BuildReadReceipt();

        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        required.Should().Contain("readable");
        schema.GetProperty("properties").GetProperty("readable").GetProperty("type").GetString().Should().Be("boolean");
    }

    [Fact]
    public void Unreadable_reason_is_a_nullable_enum_expressed_as_anyOf()
    {
        var schema = ReceiptVisionSchema.BuildReadReceipt();

        var unreadableReason = schema.GetProperty("properties").GetProperty("unreadable_reason");
        unreadableReason.TryGetProperty("type", out _).Should().BeFalse();
        var branches = unreadableReason.GetProperty("anyOf").EnumerateArray().ToList();
        branches[0].GetProperty("enum").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["too_small", "blurry", "not_a_receipt", "cut_off", "other"]);
        branches[1].GetProperty("type").GetString().Should().Be("null");
    }

    [Fact]
    public void Total_is_nullable_since_the_model_must_never_invent_a_figure_it_cannot_read()
    {
        var schema = ReceiptVisionSchema.BuildReadReceipt();

        var total = schema.GetProperty("properties").GetProperty("total");
        total.GetProperty("type").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo(["number", "null"]);
    }

    [Fact]
    public void Fiscal_number_is_a_nullable_string_required_for_strict_mode()
    {
        var schema = ReceiptVisionSchema.BuildReadReceipt();

        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        required.Should().Contain("fiscal_number");
        schema.GetProperty("properties").GetProperty("fiscal_number").GetProperty("type")
            .EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo(["string", "null"]);
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
