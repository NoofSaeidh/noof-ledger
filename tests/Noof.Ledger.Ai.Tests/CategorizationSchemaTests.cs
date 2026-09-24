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
          "required": ["items", "occurred_on", "kind", "wallet_id", "balance_amount", "balance_currency"],
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
                  "currency": { "type": ["string", "null"], "enum": ["EUR", "RSD", "USD", "RUB", "KZT", null], "description": "The currency the message states, or null when it states none." },
                  "category_slug": { "type": "string", "enum": ["groceries", "food-drink"] },
                  "merchant_name": { "type": ["string", "null"], "description": "The merchant's name as the person wrote it, or null when no merchant is named or it is one of the known merchants." }
                }
              }
            },
            "occurred_on": { "type": ["string", "null"], "description": "The day the purchase happened, as an ISO date (YYYY-MM-DD), worked out from today's date given with the message, or null when the message names no day." },
            "kind": { "type": "string", "enum": ["expense", "income", "balance"], "description": "What kind of record this is: \"expense\" for money spent, \"income\" for money received, or \"balance\" when the person states what a wallet's balance is right now rather than a purchase or a deposit - items must be empty for kind \"balance\"." },
            "wallet_id": { "type": ["string", "null"], "enum": [null], "description": "The id of the wallet the person means, chosen from the wallets you were offered, or null when no wallet is named or none of the offered wallets fits - the ledger then uses the default wallet for the spending's currency." },
            "balance_amount": { "type": ["number", "null"], "description": "The balance the person stated, as a number, when kind is \"balance\"; null for every other kind." },
            "balance_currency": { "type": ["string", "null"], "enum": ["EUR", "RSD", "USD", "RUB", "KZT", null], "description": "The currency of the stated balance, when kind is \"balance\" and the person named one; null for every other kind, or when they named none - the wallet's own currency is used then." }
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
            .Should().BeEquivalentTo(["items", "occurred_on", "kind", "wallet_id", "balance_amount", "balance_currency"]);
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

        knownMerchantId.GetProperty("type").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["string", "null"]);
        knownMerchantId.GetProperty("enum").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["11111111-1111-1111-1111-111111111111", null]);
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

        var currencies = LineItemProperties(schema).GetProperty("currency").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString());

        currencies.Should().BeEquivalentTo(["EUR", "RSD", "USD", "RUB", "KZT", null]);
    }

    [Fact]
    public void Currency_is_required_but_nullable_so_the_model_can_answer_none_stated()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, NoWallets);

        var lineItem = schema.GetProperty("properties").GetProperty("items").GetProperty("items");
        var required = lineItem.GetProperty("required").EnumerateArray().Select(e => e.GetString());

        required.Should().Contain("currency");
        LineItemProperties(schema).GetProperty("currency").GetProperty("type").EnumerateArray()
            .Select(e => e.GetString()).Should().BeEquivalentTo(["string", "null"]);
    }

    [Fact]
    public void Kind_enum_is_exactly_expense_income_balance()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, NoWallets);

        schema.GetProperty("properties").GetProperty("kind").GetProperty("type").GetString().Should().Be("string");
        schema.GetProperty("properties").GetProperty("kind").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo(["expense", "income", "balance"]);
    }

    [Fact]
    public void Wallet_id_enum_is_exactly_the_offered_wallet_ids_when_wallets_are_offered()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, OneWallet);

        var walletId = schema.GetProperty("properties").GetProperty("wallet_id");
        walletId.GetProperty("type").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo(["string", "null"]);
        walletId.GetProperty("enum").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["22222222-2222-2222-2222-222222222222", null]);
    }

    [Fact]
    public void Wallet_id_enum_is_only_null_when_no_wallets_are_offered()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, NoWallets);

        schema.GetProperty("properties").GetProperty("wallet_id").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo([(string?)null]);
    }

    [Fact]
    public void Balance_currency_enum_is_the_five_CurrencyCode_statics_plus_null()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, NoWallets);

        schema.GetProperty("properties").GetProperty("balance_currency").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo(["EUR", "RSD", "USD", "RUB", "KZT", null]);
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
        // Still exactly 2 (root + line item): kind/wallet_id/balance_amount/balance_currency are
        // scalar root properties, not a nested object - M9's decision to keep the stated balance
        // flat (plan-00-header.md, "expensive to reverse" #5) is exactly what keeps this count from
        // growing to 3.
        var json = CategorizationSchema.BuildRecordTransaction(Categories, OneHint, OneWallet).GetRawText();

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
        var recordTransaction = CategorizationSchema.BuildRecordTransaction(Categories, OneHint, OneWallet).GetRawText();
        var listMerchants = CategorizationSchema.BuildListMerchants().GetRawText();

        recordTransaction.Should().NotContain(forbidden);
        listMerchants.Should().NotContain(forbidden);
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
