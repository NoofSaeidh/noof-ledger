using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Noof.Ledger.Application.Categorization;

namespace Noof.Ledger.Ai.Tests;

public class CategorizationSchemaTests
{
    static readonly IReadOnlyList<CategoryOption> Categories =
    [
        new CategoryOption("groceries", "Groceries", "Продукты", null),
        new CategoryOption("food-drink", "Food & Drink", "Еда и напитки", null),
    ];

    static readonly IReadOnlyList<MerchantOption> NoHints = [];

    static readonly IReadOnlyList<MerchantOption> OneHint =
    [
        new MerchantOption(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Lidl"),
    ];

    [Fact]
    public void Record_spending_with_no_hints_matches_the_pinned_shape()
    {
        var schema = CategorizationSchema.BuildRecordSpending(Categories, NoHints);

        const string expected = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["items"],
          "properties": {
            "items": {
              "type": "array",
              "minItems": 0,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["description", "amount_quote", "category_slug"],
                "properties": {
                  "description": { "type": "string", "description": "What was bought, as short plain text in the language of the message." },
                  "amount_quote": { "type": "string", "description": "The amount copied from the message character for character, exactly as written. Do not convert digits, do not add or remove separators, do not add a currency symbol, and never compute or sum anything. If the message does not state an amount for this line, do not produce the line." },
                  "currency": { "type": "string", "enum": ["EUR", "RSD", "USD", "RUB", "KZT"] },
                  "category_slug": { "type": "string", "enum": ["groceries", "food-drink"] },
                  "merchant_quote": { "type": "string", "description": "The merchant name copied from the message character for character. Only when a merchant is actually named and it is not one of the known merchants." }
                }
              }
            }
          }
        }
        """;

        JsonNode.DeepEquals(Reserialize(schema), JsonNode.Parse(expected)).Should().BeTrue();
    }

    [Fact]
    public void An_empty_hint_list_omits_known_merchant_id_entirely()
    {
        var schema = CategorizationSchema.BuildRecordSpending(Categories, NoHints);

        LineItemProperties(schema).TryGetProperty("known_merchant_id", out _).Should().BeFalse();
    }

    [Fact]
    public void With_hints_known_merchant_id_lands_between_category_slug_and_merchant_quote()
    {
        var schema = CategorizationSchema.BuildRecordSpending(Categories, OneHint);

        var names = LineItemProperties(schema).EnumerateObject().Select(p => p.Name);

        names.Should().ContainInOrder("category_slug", "known_merchant_id", "merchant_quote");
    }

    [Fact]
    public void Known_merchant_id_enum_is_exactly_the_hinted_guids()
    {
        var schema = CategorizationSchema.BuildRecordSpending(Categories, OneHint);

        var knownMerchantId = LineItemProperties(schema).GetProperty("known_merchant_id");

        knownMerchantId.GetProperty("type").GetString().Should().Be("string");
        knownMerchantId.GetProperty("enum").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["11111111-1111-1111-1111-111111111111"]);
        knownMerchantId.GetProperty("description").GetString().Should().Be(
            "Set this only if the merchant in the message is one of the listed known merchants.");
    }

    [Fact]
    public void Category_slug_enum_is_exactly_the_offered_slugs_and_nothing_else()
    {
        var schema = CategorizationSchema.BuildRecordSpending(Categories, NoHints);

        var slugs = LineItemProperties(schema).GetProperty("category_slug").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString());

        slugs.Should().BeEquivalentTo(Categories.Select(c => c.Slug));
    }

    [Fact]
    public void Currency_enum_is_the_five_CurrencyCode_statics()
    {
        var schema = CategorizationSchema.BuildRecordSpending(Categories, NoHints);

        var currencies = LineItemProperties(schema).GetProperty("currency").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString());

        currencies.Should().BeEquivalentTo(["EUR", "RSD", "USD", "RUB", "KZT"]);
    }

    [Fact]
    public void Currency_is_a_property_but_is_not_required()
    {
        // The model must be able to answer "not stated" by omitting currency altogether — it is
        // still constrained to one of the five codes whenever it does report one (see
        // Currency_enum_is_the_five_CurrencyCode_statics), but it is never forced to invent a
        // sixth answer for a message that states no currency at all.
        var schema = CategorizationSchema.BuildRecordSpending(Categories, NoHints);

        var lineItem = schema.GetProperty("properties").GetProperty("items").GetProperty("items");
        var required = lineItem.GetProperty("required").EnumerateArray().Select(e => e.GetString());

        required.Should().NotContain("currency");
        LineItemProperties(schema).TryGetProperty("currency", out _).Should().BeTrue();
    }

    [Fact]
    public void List_merchants_matches_the_pinned_shape()
    {
        var schema = CategorizationSchema.BuildListMerchants();

        const string expected =
            """{ "type": "object", "additionalProperties": false, "properties": {}, "required": [] }""";

        JsonNode.DeepEquals(Reserialize(schema), JsonNode.Parse(expected)).Should().BeTrue();
    }

    [Fact]
    public void Additional_properties_false_appears_at_every_object_level_of_record_spending()
    {
        var json = CategorizationSchema.BuildRecordSpending(Categories, OneHint).GetRawText();

        CountOccurrences(json, "\"additionalProperties\":false").Should().Be(2);
    }

    [Fact]
    public void Additional_properties_false_appears_on_list_merchants()
    {
        var json = CategorizationSchema.BuildListMerchants().GetRawText();

        CountOccurrences(json, "\"additionalProperties\":false").Should().Be(1);
    }

    [Theory]
    [InlineData("minLength")]
    [InlineData("maxLength")]
    [InlineData("\"minimum\"")]
    [InlineData("\"maximum\"")]
    [InlineData("multipleOf")]
    [InlineData("$ref")]
    [InlineData("pattern")]
    public void No_unsupported_json_schema_keyword_appears_anywhere(string forbidden)
    {
        var recordSpending = CategorizationSchema.BuildRecordSpending(Categories, OneHint).GetRawText();
        var listMerchants = CategorizationSchema.BuildListMerchants().GetRawText();

        recordSpending.Should().NotContain(forbidden);
        listMerchants.Should().NotContain(forbidden);
    }

    [Fact]
    public void Every_minItems_value_is_zero_or_one()
    {
        var json = CategorizationSchema.BuildRecordSpending(Categories, OneHint).GetRawText();

        foreach (Match match in Regex.Matches(json, "\"minItems\":(\\d+)"))
            match.Groups[1].Value.Should().BeOneOf("0", "1");
    }

    [Fact]
    public void Same_inputs_produce_byte_identical_json_twice()
    {
        var first = CategorizationSchema.BuildRecordSpending(Categories, OneHint).GetRawText();
        var second = CategorizationSchema.BuildRecordSpending([.. Categories], [.. OneHint]).GetRawText();

        first.Should().Be(second);
    }

    [Fact]
    public void Schema_round_trips_through_JsonElement_deserialization_unchanged()
    {
        // The schema is handed to ChatResponseFormat.ForJsonSchema as a JsonElement directly, not
        // reassembled from a dictionary — this pins the property that call site actually relies on.
        var schema = CategorizationSchema.BuildRecordSpending(Categories, OneHint);
        var roundTripped = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(schema));

        JsonNode.DeepEquals(JsonNode.Parse(roundTripped.GetRawText()), JsonNode.Parse(schema.GetRawText())).Should().BeTrue();
    }

    static JsonElement LineItemProperties(JsonElement schema) =>
        schema.GetProperty("properties").GetProperty("items").GetProperty("items").GetProperty("properties");

    static JsonNode Reserialize(JsonElement schema) => JsonNode.Parse(schema.GetRawText())!;

    static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
