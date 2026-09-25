using System.Text.Json;
using System.Text.Json.Nodes;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Ai;

// Raw JSON Schema, built as a JsonElement rather than through a typed builder or reflection: a
// strict raw-schema AIFunctionDeclaration needs "additionalProperties": false at every object
// level, which is easiest to guarantee by building the JsonObject tree directly and controlling
// every key by hand.
internal static class CategorizationSchema
{
    const string AmountDescription =
        "The amount the person meant, as a number - for example 1000 or 45.3. Interpret words, slang and speech: "
        + "\"штуку\" is 1000, \"полтос\" is 50, \"двести пятьдесят\" is 250.";

    const string OccurredOnDescription =
        "The day the purchase happened, as an ISO date (YYYY-MM-DD), worked out from today's date given with the "
        + "message, or null when the message names no day.";

    const string KindDescription =
        "What kind of record this is: \"expense\" for money spent, \"income\" for money received, or "
        + "\"balance\" when the person states what a wallet's balance is right now rather than a purchase "
        + "or a deposit - items must be empty for kind \"balance\".";

    const string WalletIdDescription =
        "The id of the wallet the person means, chosen from the wallets you were offered, or null when no "
        + "wallet is named or none of the offered wallets fits - the ledger then uses the default wallet "
        + "for the spending's currency.";

    const string BalanceAmountDescription =
        "The balance the person stated, as a number, when kind is \"balance\"; null for every other kind.";

    const string BalanceCurrencyDescription =
        "The currency of the stated balance, when kind is \"balance\" and the person named one; null for "
        + "every other kind, or when they named none - the wallet's own currency is used then.";

    public static JsonElement BuildRecordTransaction(
        IReadOnlyList<CategoryOption> categories, IReadOnlyList<MerchantOption> merchantHints, IReadOnlyList<WalletOption> wallets)
    {
        var properties = new List<KeyValuePair<string, JsonNode?>>
        {
            new("description", new JsonObject
            {
                ["type"] = "string",
                ["description"] = "What was bought, as short plain text in the language of the message.",
            }),
            new("amount", new JsonObject
            {
                ["type"] = "number",
                ["description"] = AmountDescription,
            }),
            // Strict mode puts every declared property in "required" (below), so optionality is a
            // nullable type instead of omission (operator, 2026-09-23).
            new("currency", NullableStringEnum(
                CurrencyCode.Supported.Select(code => code.Value),
                "The currency the message states, or null when it states none.")),
            new("category_slug", new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(categories.Select(c => (JsonNode)c.Slug).ToArray()),
            }),
        };

        if (merchantHints.Count > 0)
        {
            properties.Add(new("known_merchant_id", NullableStringEnum(
                merchantHints.Select(m => m.Id.ToString()),
                "One of the listed known merchants' ids, or null when the merchant is not one of them.")));
        }

        properties.Add(new("merchant_name", new JsonObject
        {
            ["type"] = new JsonArray("string", "null"),
            ["description"] = "The merchant's name as the person wrote it, or null when no merchant is named or it is one of the known merchants.",
        }));

        var lineItem = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            // Derived from the properties actually added, not a hand-written list: strict mode
            // requires every declared property here, and this keeps known_merchant_id out of it on
            // the no-hints path, where the property itself is never declared (operator, 2026-09-23).
            ["required"] = new JsonArray([.. properties.Select(p => (JsonNode)p.Key)]),
            ["properties"] = new JsonObject(properties),
        };

        var root = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            // kind/wallet_id/balance_amount/balance_currency are root properties, not line-item
            // ones (M3, M9): one message records one transaction, in one wallet, of one kind.
            ["required"] = new JsonArray("items", "occurred_on", "kind", "wallet_id", "balance_amount", "balance_currency"),
            ["properties"] = new JsonObject
            {
                ["items"] = new JsonObject
                {
                    ["type"] = "array",
                    // 0, not 1: every "balance" answer has no items at all (there is nothing to
                    // categorise, only a balance to state), and a message can otherwise mention a
                    // figure with nothing to record against it. Only 0 and 1 are valid values for
                    // minItems under this API's schema subset.
                    ["minItems"] = 0,
                    ["items"] = lineItem,
                },
                ["occurred_on"] = new JsonObject
                {
                    ["type"] = new JsonArray("string", "null"),
                    ["description"] = OccurredOnDescription,
                },
                ["kind"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("expense", "income", "balance"),
                    ["description"] = KindDescription,
                },
                // Declared even with zero wallets offered (rather than omitted, as known_merchant_id
                // is on the no-hints path): wallet_id is a root property that always exists on this
                // tool, so with nothing to choose from it narrows to a value that can only be null.
                ["wallet_id"] = wallets.Count > 0
                    ? NullableStringEnum(wallets.Select(w => w.Id.ToString()), WalletIdDescription)
                    : new JsonObject { ["type"] = "null", ["description"] = WalletIdDescription },
                ["balance_amount"] = new JsonObject
                {
                    ["type"] = new JsonArray("number", "null"),
                    ["description"] = BalanceAmountDescription,
                },
                ["balance_currency"] = NullableStringEnum(
                    CurrencyCode.Supported.Select(code => code.Value), BalanceCurrencyDescription),
            },
        };

        return ToElement(root);
    }

    public static JsonElement BuildListMerchants()
    {
        var root = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject(),
            ["required"] = new JsonArray(),
        };

        return ToElement(root);
    }

    // An anyOf, not "type": ["string", "null"] beside the enum: the API checks every enum value
    // against the type array as a whole and rejects the tool with a 400 ("Enum value 'EUR' does
    // not match declared type '['string', 'null']'"), which failed every capture after Phase 4.
    static JsonObject NullableStringEnum(IEnumerable<string> values, string description) => new()
    {
        ["anyOf"] = new JsonArray(
            new JsonObject { ["type"] = "string", ["enum"] = new JsonArray([.. values.Select(value => (JsonNode)value)]) },
            new JsonObject { ["type"] = "null" }),
        ["description"] = description,
    };

    static JsonElement ToElement(JsonObject root)
    {
        using var document = JsonDocument.Parse(root.ToJsonString());
        return document.RootElement.Clone();
    }
}
