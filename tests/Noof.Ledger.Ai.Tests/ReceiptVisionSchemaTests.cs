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
            .Should().BeEquivalentTo(["sale", "refund", "exchange"]);
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

    [Fact]
    public void Exchange_is_a_required_object_whose_every_field_is_required_and_nullable()
    {
        var schema = ReceiptVisionSchema.BuildReadReceipt();

        schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().Contain("exchange");
        var exchange = schema.GetProperty("properties").GetProperty("exchange");
        exchange.GetProperty("type").GetString().Should().Be("object",
            "a nullable exchange would cost one union-type parameter more than the API allows; a non-slip answers it with every field null");
        exchange.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();

        string[] fields = ["given_amount", "given_currency", "received_amount", "received_currency", "rate", "commission", "slip_number"];
        exchange.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo(fields);
        var properties = exchange.GetProperty("properties");
        properties.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(fields);

        foreach (var number in new[] { "given_amount", "received_amount", "rate" })
        {
            properties.GetProperty(number).GetProperty("type").EnumerateArray().Select(e => e.GetString())
                .Should().BeEquivalentTo(["number", "null"]);
        }

        foreach (var text in new[] { "given_currency", "received_currency", "slip_number" })
        {
            properties.GetProperty(text).GetProperty("type").EnumerateArray().Select(e => e.GetString())
                .Should().BeEquivalentTo(["string", "null"]);
        }
    }

    [Fact]
    public void Commission_is_one_nullable_object_holding_its_amount_and_currency()
    {
        var commission = ReceiptVisionSchema.BuildReadReceipt()
            .GetProperty("properties").GetProperty("exchange").GetProperty("properties").GetProperty("commission");

        commission.TryGetProperty("type", out _).Should().BeFalse();
        var branches = commission.GetProperty("anyOf").EnumerateArray().ToList();
        branches.Should().HaveCount(2);
        branches[1].GetProperty("type").GetString().Should().Be("null");
        var present = branches[0];
        present.GetProperty("type").GetString().Should().Be("object");
        present.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        present.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo(["amount", "currency"]);
        present.GetProperty("properties").GetProperty("amount").GetProperty("type").GetString().Should().Be("number");
        present.GetProperty("properties").GetProperty("currency").GetProperty("type").GetString().Should().Be("string");
    }

    [Fact]
    public void Not_a_receipt_is_described_as_neither_a_shop_receipt_nor_an_exchange_office_slip()
    {
        var properties = ReceiptVisionSchema.BuildReadReceipt().GetProperty("properties");

        properties.GetProperty("unreadable_reason").GetProperty("description").GetString()
            .Should().Contain("neither a shop receipt nor an exchange-office slip");
        properties.GetProperty("readable").GetProperty("description").GetString()
            .Should().Contain("neither a shop receipt nor an exchange-office slip");
    }

    // The API's documented ceiling (structured outputs, "Parameters with union types": 16 across all
    // strict schemas of one request) - read_receipt is the only tool on its request. One over it and
    // every vision call is a 400, receipts included.
    [Fact]
    public void Read_receipt_stays_within_the_sixteen_union_type_parameters_the_API_allows()
    {
        var schema = Reserialize(ReceiptVisionSchema.BuildReadReceipt());

        var unions = PropertySchemas(schema).Count(property => property["anyOf"] is not null || property["type"] is JsonArray);

        unions.Should().BeLessThanOrEqualTo(16);
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

    static IEnumerable<JsonObject> PropertySchemas(JsonNode schema) =>
        Descendants(schema).OfType<JsonObject>()
            .Select(node => node["properties"]).OfType<JsonObject>()
            .SelectMany(properties => properties.Select(property => property.Value).OfType<JsonObject>());

    static JsonNode Reserialize(JsonElement schema) => JsonNode.Parse(schema.GetRawText())!;
}
