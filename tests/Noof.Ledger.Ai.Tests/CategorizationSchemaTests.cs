using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;

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

    static readonly IReadOnlyList<WalletOption> NoWallets = [];

    static readonly IReadOnlyList<WalletOption> OneWallet =
    [
        new WalletOption(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Cash", CurrencyCode.Rsd, [], true),
    ];

    [Fact]
    public void Record_transaction_with_no_hints_or_wallets_matches_the_pinned_shape()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, NoWallets);

        const string expected = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["items", "occurred_on", "kind", "wallet_id", "balance_amount", "balance_currency", "transfer", "charged"],
          "properties": {
            "items": {
              "type": "array",
              "minItems": 0,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["description", "amount", "currency", "category_slug", "merchant_name"],
                "properties": {
                  "description": { "type": "string", "description": "What was bought, as short plain text in the language of the message." },
                  "amount": { "type": "number", "description": "The amount the person meant, as a number - for example 1000 or 45.3. Interpret words, slang and speech: \"штуку\" is 1000, \"полтос\" is 50, \"двести пятьдесят\" is 250." },
                  "currency": { "anyOf": [{ "type": "string", "enum": ["EUR", "RSD", "USD", "RUB", "KZT"] }, { "type": "null" }], "description": "The currency the message states, or null when it states none." },
                  "category_slug": { "type": "string", "enum": ["groceries", "food-drink"] },
                  "merchant_name": { "type": ["string", "null"], "description": "The merchant's name as the person wrote it, or null when no merchant is named or it is one of the known merchants." }
                }
              }
            },
            "occurred_on": { "type": ["string", "null"], "description": "The day the purchase happened, as an ISO date (YYYY-MM-DD), worked out from today's date given with the message, or null when the message names no day." },
            "kind": { "type": "string", "enum": ["expense", "income", "balance", "transfer"], "description": "What kind of record this is: \"expense\" for money spent, \"income\" for money received, \"balance\" when the person states what a wallet's balance is right now rather than a purchase or a deposit, or \"transfer\" when money moves between the person's own wallets - a withdrawal, a top-up, a transfer between their accounts or a currency exchange. items must be empty for kind \"balance\" and kind \"transfer\"." },
            "wallet_id": { "type": "null", "description": "The id of the wallet the person means, chosen from the wallets you were offered, or null when no wallet is named or none of the offered wallets fits - the ledger then uses the default wallet for the spending's currency." },
            "balance_amount": { "type": ["number", "null"], "description": "The balance the person stated, as a number, when kind is \"balance\"; null for every other kind." },
            "balance_currency": { "anyOf": [{ "type": "string", "enum": ["EUR", "RSD", "USD", "RUB", "KZT"] }, { "type": "null" }], "description": "The currency of the stated balance, when kind is \"balance\" and the person named one; null for every other kind, or when they named none - the wallet's own currency is used then." },
            "transfer": {
              "anyOf": [
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["from_wallet_id", "from_amount", "from_currency", "to_wallet_id", "to_amount", "to_currency", "rate", "fee"],
                  "properties": {
                    "from_wallet_id": { "type": "null", "description": "The id of the wallet the money left, chosen from the wallets you were offered, or null when the person names none - the ledger then uses the card wallet of from_currency, or else its default wallet." },
                    "from_amount": { "type": "number", "description": "The amount that left, as the person said it - never worked out from other figures." },
                    "from_currency": { "type": "string", "enum": ["EUR", "RSD", "USD", "RUB", "KZT"], "description": "The currency of the amount that left." },
                    "to_wallet_id": { "type": "null", "description": "The id of the wallet the money arrived in, chosen from the wallets you were offered, or null when the person names none - the ledger then uses the cash wallet of to_currency, or else its default wallet." },
                    "to_amount": { "type": ["number", "null"], "description": "The amount that arrived, as the person said it, or null when they did not say it - never worked out from a rate." },
                    "to_currency": { "type": "string", "enum": ["EUR", "RSD", "USD", "RUB", "KZT"], "description": "The currency the money arrived in." },
                    "rate": {
                      "anyOf": [
                        {
                          "type": "object",
                          "additionalProperties": false,
                          "required": ["base_currency", "quote_amount", "quote_currency"],
                          "properties": {
                            "base_currency": { "type": "string", "enum": ["EUR", "RSD", "USD", "RUB", "KZT"], "description": "The currency the rate is quoted per one unit of." },
                            "quote_amount": { "type": "number", "description": "How many quote_currency units one base_currency unit is, as stated." },
                            "quote_currency": { "type": "string", "enum": ["EUR", "RSD", "USD", "RUB", "KZT"], "description": "The currency the rate is quoted in." }
                          }
                        },
                        { "type": "null" }
                      ],
                      "description": "The exchange rate the person stated, read as 1 base_currency = quote_amount quote_currency, or null when they stated none."
                    },
                    "fee": {
                      "anyOf": [
                        {
                          "type": "object",
                          "additionalProperties": false,
                          "required": ["amount", "currency", "leg", "included"],
                          "properties": {
                            "amount": { "type": "number", "description": "The fee, as the person said it." },
                            "currency": { "type": "string", "enum": ["EUR", "RSD", "USD", "RUB", "KZT"], "description": "The fee's currency." },
                            "leg": { "type": "string", "enum": ["from", "to"], "description": "\"from\" unless the person says the receiving side kept the fee - then \"to\"; a fee in only one side's currency is always on that side." },
                            "included": { "type": "boolean", "description": "True only when the person says the amount they gave for that side already includes the fee, or when the fee is on \"to\" and to_amount is what they say arrived." }
                          }
                        },
                        { "type": "null" }
                      ],
                      "description": "The fee the person stated for this transfer, or null when they stated none."
                    }
                  }
                },
                { "type": "null" }
              ],
              "description": "What moved, when kind is \"transfer\": the side the money left (from_*) and the side it arrived on (to_*), with every amount exactly as the person said it; null for every other kind."
            },
            "charged": {
              "anyOf": [
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["amount", "currency", "fee_amount", "fee_included"],
                  "properties": {
                    "amount": { "type": "number", "description": "The charged amount, as the person said it." },
                    "currency": { "type": "string", "enum": ["EUR", "RSD", "USD", "RUB", "KZT"], "description": "The wallet's currency the amount was charged in." },
                    "fee_amount": { "type": ["number", "null"], "description": "The commission the person named for that charge, or null when they named none." },
                    "fee_included": { "type": "boolean", "description": "True only when the person says the charged amount includes that commission." }
                  }
                },
                { "type": "null" }
              ],
              "description": "What the wallet was actually charged, in its own currency, for a purchase in another currency - only when the person says it; null otherwise."
            }
          }
        }
        """;

        JsonNode.DeepEquals(Reserialize(schema), JsonNode.Parse(expected)).Should().BeTrue();
    }

    [Fact]
    public void Occurred_on_is_required_but_nullable_so_the_model_can_answer_no_day_named()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, NoWallets);

        schema.GetProperty("properties").TryGetProperty("occurred_on", out var occurredOn).Should().BeTrue();
        occurredOn.GetProperty("type").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo(["string", "null"]);
        schema.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["items", "occurred_on", "kind", "wallet_id", "balance_amount", "balance_currency", "transfer", "charged"]);
    }

    [Fact]
    public void An_empty_hint_list_omits_known_merchant_id_entirely()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, NoWallets);

        LineItemProperties(schema).TryGetProperty("known_merchant_id", out _).Should().BeFalse();
    }

    [Fact]
    public void With_hints_known_merchant_id_lands_between_category_slug_and_merchant_name()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, OneHint, NoWallets);

        var names = LineItemProperties(schema).EnumerateObject().Select(p => p.Name);

        names.Should().ContainInOrder("category_slug", "known_merchant_id", "merchant_name");
    }

    [Fact]
    public void Known_merchant_id_enum_is_exactly_the_hinted_guids()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, OneHint, NoWallets);

        var knownMerchantId = LineItemProperties(schema).GetProperty("known_merchant_id");

        NullableStringEnumValues(knownMerchantId).Should().BeEquivalentTo(["11111111-1111-1111-1111-111111111111"]);
        knownMerchantId.GetProperty("description").GetString().Should().Be(
            "One of the listed known merchants' ids, or null when the merchant is not one of them.");
    }

    [Fact]
    public void Category_slug_enum_is_exactly_the_offered_slugs_and_nothing_else()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, NoWallets);

        var slugs = LineItemProperties(schema).GetProperty("category_slug").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString());

        slugs.Should().BeEquivalentTo(Categories.Select(c => c.Slug));
    }

    [Fact]
    public void Currency_enum_is_the_five_CurrencyCode_statics()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, NoWallets);

        NullableStringEnumValues(LineItemProperties(schema).GetProperty("currency"))
            .Should().BeEquivalentTo(["EUR", "RSD", "USD", "RUB", "KZT"]);
    }

    [Fact]
    public void Currency_is_required_but_nullable_so_the_model_can_answer_none_stated()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, NoWallets);

        var lineItem = schema.GetProperty("properties").GetProperty("items").GetProperty("items");
        var required = lineItem.GetProperty("required").EnumerateArray().Select(e => e.GetString());

        required.Should().Contain("currency");
        NullableStringEnumValues(LineItemProperties(schema).GetProperty("currency")).Should().NotBeEmpty();
    }

    [Fact]
    public void Kind_enum_is_exactly_expense_income_balance_and_transfer()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, NoWallets);

        schema.GetProperty("properties").GetProperty("kind").GetProperty("type").GetString().Should().Be("string");
        schema.GetProperty("properties").GetProperty("kind").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).Should().Equal("expense", "income", "balance", "transfer");
    }

    [Fact]
    public void Wallet_id_enum_is_exactly_the_offered_wallet_ids_when_wallets_are_offered()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, OneWallet);

        var walletId = schema.GetProperty("properties").GetProperty("wallet_id");
        NullableStringEnumValues(walletId).Should().BeEquivalentTo(["22222222-2222-2222-2222-222222222222"]);
    }

    [Fact]
    public void Wallet_id_can_only_be_null_when_no_wallets_are_offered()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, NoWallets);

        schema.GetProperty("properties").GetProperty("wallet_id").GetProperty("type").GetString().Should().Be("null");
    }

    [Fact]
    public void Balance_currency_enum_is_the_five_CurrencyCode_statics_or_null()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, NoWallets);

        NullableStringEnumValues(schema.GetProperty("properties").GetProperty("balance_currency"))
            .Should().BeEquivalentTo(["EUR", "RSD", "USD", "RUB", "KZT"]);
    }

    [Fact]
    public void Balance_amount_is_a_nullable_number_with_no_bound()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, NoWallets);

        schema.GetProperty("properties").GetProperty("balance_amount").GetProperty("type")
            .EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo(["number", "null"]);
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
    public void Additional_properties_false_appears_at_every_object_level_of_record_transaction()
    {
        // 6: root, line item, transfer, its rate, its fee, charged. M9 kept the stated balance flat, and kind, wallet_id
        // and balance_* stay flat root properties. A transfer is different: two sides with a wallet, an amount and a
        // currency each, plus a rate and a fee that are objects in their own right - flat, that is a dozen transfer_*
        // nullables beside the expense properties, every one meaningless for an expense. So transfer and charged are
        // nested, each an anyOf over a strict object and null (NullableObject), and every one of those objects must
        // carry additionalProperties: false itself - which is what this count pins.
        var json = CategorizationSchema.BuildRecordTransaction(Categories, OneHint, OneWallet).GetRawText();

        CountOccurrences(json, "\"additionalProperties\":false").Should().Be(6);
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
        var recordTransaction = CategorizationSchema.BuildRecordTransaction(Categories, OneHint, OneWallet).GetRawText();
        var listMerchants = CategorizationSchema.BuildListMerchants().GetRawText();

        recordTransaction.Should().NotContain(forbidden);
        listMerchants.Should().NotContain(forbidden);
    }

    // The API rejects an enum next to a type array - "Enum value 'EUR' does not match declared type
    // '['string', 'null']'" (live 400, 2026-09-25) - so every nullable enum is an anyOf instead.
    [Fact]
    public void No_node_declares_an_enum_alongside_a_type_array()
    {
        var schema = Reserialize(CategorizationSchema.BuildRecordTransaction(Categories, OneHint, OneWallet));

        Descendants(schema).OfType<JsonObject>()
            .Where(node => node["enum"] is not null && node["type"] is JsonArray)
            .Should().BeEmpty();
    }

    [Fact]
    public void Every_minItems_value_is_zero_or_one()
    {
        var json = CategorizationSchema.BuildRecordTransaction(Categories, OneHint, OneWallet).GetRawText();

        foreach (Match match in Regex.Matches(json, "\"minItems\":(\\d+)"))
            match.Groups[1].Value.Should().BeOneOf("0", "1");
    }

    [Fact]
    public void Same_inputs_produce_byte_identical_json_twice()
    {
        var first = CategorizationSchema.BuildRecordTransaction(Categories, OneHint, OneWallet).GetRawText();
        var second = CategorizationSchema.BuildRecordTransaction([.. Categories], [.. OneHint], [.. OneWallet]).GetRawText();

        first.Should().Be(second);
    }

    [Fact]
    public void Schema_round_trips_through_JsonElement_deserialization_unchanged()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, OneHint, OneWallet);
        var roundTripped = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(schema));

        JsonNode.DeepEquals(JsonNode.Parse(roundTripped.GetRawText()), JsonNode.Parse(schema.GetRawText())).Should().BeTrue();
    }

    [Fact]
    public void Transfer_is_a_nullable_object_whose_every_property_is_required()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, OneWallet);

        var transfer = ObjectBranch(schema.GetProperty("properties").GetProperty("transfer"));

        transfer.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        transfer.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().Equal(
            "from_wallet_id", "from_amount", "from_currency", "to_wallet_id", "to_amount", "to_currency", "rate", "fee");
        transfer.GetProperty("properties").EnumerateObject().Select(p => p.Name).Should().Equal(
            "from_wallet_id", "from_amount", "from_currency", "to_wallet_id", "to_amount", "to_currency", "rate", "fee");
    }

    [Fact]
    public void Each_transfer_legs_wallet_id_is_one_of_the_offered_wallet_ids_or_null()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, OneWallet);

        var properties = ObjectBranch(schema.GetProperty("properties").GetProperty("transfer")).GetProperty("properties");

        NullableStringEnumValues(properties.GetProperty("from_wallet_id")).Should().Equal("22222222-2222-2222-2222-222222222222");
        NullableStringEnumValues(properties.GetProperty("to_wallet_id")).Should().Equal("22222222-2222-2222-2222-222222222222");
    }

    [Fact]
    public void Each_transfer_legs_wallet_id_can_only_be_null_when_no_wallets_are_offered()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, NoWallets);

        var properties = ObjectBranch(schema.GetProperty("properties").GetProperty("transfer")).GetProperty("properties");

        properties.GetProperty("from_wallet_id").GetProperty("type").GetString().Should().Be("null");
        properties.GetProperty("to_wallet_id").GetProperty("type").GetString().Should().Be("null");
    }

    [Fact]
    public void Transfer_amounts_are_numbers_and_only_the_received_one_may_be_null()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, NoWallets);

        var properties = ObjectBranch(schema.GetProperty("properties").GetProperty("transfer")).GetProperty("properties");

        properties.GetProperty("from_amount").GetProperty("type").GetString().Should().Be("number");
        properties.GetProperty("to_amount").GetProperty("type").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("number", "null");
    }

    [Fact]
    public void Transfer_currencies_are_the_supported_codes_and_never_null()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, NoWallets);

        var properties = ObjectBranch(schema.GetProperty("properties").GetProperty("transfer")).GetProperty("properties");

        foreach (var name in new[] { "from_currency", "to_currency" })
        {
            properties.GetProperty(name).GetProperty("type").GetString().Should().Be("string", name);
            properties.GetProperty(name).GetProperty("enum").EnumerateArray().Select(e => e.GetString())
                .Should().Equal("EUR", "RSD", "USD", "RUB", "KZT");
        }
    }

    [Fact]
    public void Rate_and_fee_are_nullable_strict_objects_inside_the_transfer()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, NoWallets);

        var properties = ObjectBranch(schema.GetProperty("properties").GetProperty("transfer")).GetProperty("properties");
        var rate = ObjectBranch(properties.GetProperty("rate"));
        var fee = ObjectBranch(properties.GetProperty("fee"));

        rate.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        rate.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("base_currency", "quote_amount", "quote_currency");
        fee.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        fee.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("amount", "currency", "leg", "included");
        fee.GetProperty("properties").GetProperty("leg").GetProperty("enum").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("from", "to");
        fee.GetProperty("properties").GetProperty("included").GetProperty("type").GetString().Should().Be("boolean");
    }

    [Fact]
    public void Charged_is_a_nullable_strict_object_with_a_nullable_fee_amount()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, NoWallets);

        var charged = ObjectBranch(schema.GetProperty("properties").GetProperty("charged"));

        charged.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        charged.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("amount", "currency", "fee_amount", "fee_included");
        charged.GetProperty("properties").GetProperty("fee_amount").GetProperty("type").EnumerateArray()
            .Select(e => e.GetString()).Should().Equal("number", "null");
    }

    // The same reason NullableEnum exists: a nullable object is an anyOf, never "type": ["object", "null"].
    [Fact]
    public void No_node_declares_object_inside_a_type_array()
    {
        var schema = Reserialize(CategorizationSchema.BuildRecordTransaction(Categories, OneHint, OneWallet));

        Descendants(schema).OfType<JsonObject>()
            .Where(node => node["type"] is JsonArray types && types.Any(type => (string?)type == "object"))
            .Should().BeEmpty();
    }

    // Anthropic's strict tool use accepts at most 16 parameters with a union type (an anyOf, or a type array such
    // as ["number", "null"]) across the tools of one request; one more and every capture call is a 400. record_transaction
    // and list_merchants are always offered together, so they are counted together, in the largest case: hints and
    // wallets offered. A parameter is any property schema, at any depth.
    [Fact]
    public void Record_transaction_and_list_merchants_have_at_most_16_union_typed_parameters()
    {
        var tools = new[]
        {
            Reserialize(CategorizationSchema.BuildRecordTransaction(Categories, OneHint, OneWallet)),
            Reserialize(CategorizationSchema.BuildListMerchants()),
        };

        var unionTyped = tools
            .SelectMany(Descendants)
            .OfType<JsonObject>()
            .Select(node => node["properties"])
            .OfType<JsonObject>()
            .SelectMany(properties => properties.Select(property => property.Value))
            .OfType<JsonObject>()
            .Count(property => property["anyOf"] is not null || property["type"] is JsonArray);

        unionTyped.Should().BeLessThanOrEqualTo(16);
    }

    static JsonElement ObjectBranch(JsonElement nullableObject)
    {
        nullableObject.TryGetProperty("type", out _).Should().BeFalse("a nullable object is an anyOf, not a type array");
        var branches = nullableObject.GetProperty("anyOf").EnumerateArray().ToList();
        branches.Should().HaveCount(2);
        branches[0].GetProperty("type").GetString().Should().Be("object");
        branches[1].GetProperty("type").GetString().Should().Be("null");
        return branches[0];
    }

    static JsonElement LineItemProperties(JsonElement schema) =>
        schema.GetProperty("properties").GetProperty("items").GetProperty("items").GetProperty("properties");

    static IEnumerable<string?> NullableStringEnumValues(JsonElement property)
    {
        var branches = property.GetProperty("anyOf").EnumerateArray().ToList();
        branches.Should().HaveCount(2);
        branches[0].GetProperty("type").GetString().Should().Be("string");
        branches[1].GetProperty("type").GetString().Should().Be("null");
        return branches[0].GetProperty("enum").EnumerateArray().Select(e => e.GetString());
    }

    static IEnumerable<JsonNode> Descendants(JsonNode node) => node switch
    {
        JsonObject obj => [obj, .. obj.Where(p => p.Value is not null).SelectMany(p => Descendants(p.Value!))],
        JsonArray array => [array, .. array.Where(item => item is not null).SelectMany(item => Descendants(item!))],
        _ => [node],
    };

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
