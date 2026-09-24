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
            new("currency", new JsonObject
            {
                // Strict mode puts every declared property in "required" (below); optionality is a
                // nullable type instead of omission, and the enum keyword still applies to a null
                // value, so null has to be listed in it explicitly too (operator, 2026-09-23).
                ["type"] = new JsonArray("string", "null"),
                ["enum"] = new JsonArray([.. CurrencyCode.Supported.Select(code => (JsonNode)code.Value), null]),
                ["description"] = "The currency the message states, or null when it states none.",
            }),
            new("category_slug", new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(categories.Select(c => (JsonNode)c.Slug).ToArray()),
            }),
        };

        if (merchantHints.Count > 0)
        {
            properties.Add(new("known_merchant_id", new JsonObject
            {
                ["type"] = new JsonArray("string", "null"),
                ["enum"] = new JsonArray([.. merchantHints.Select(m => (JsonNode)m.Id.ToString()), null]),
                ["description"] = "One of the listed known merchants' ids, or null when the merchant is not one of them.",
            }));
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
                    // 0, not 1: a message can genuinely describe zero purchases (a loan received,
                    // not a purchase - see CategorizationPrompt's "заняла у Маши" example, and every
                    // "balance" answer, which never has items at all). Only 0 and 1 are valid values
                    // for minItems under this API's schema subset.
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
                ["wallet_id"] = new JsonObject
                {
                    // Kept as a nullable-string enum even with zero wallets offered (rather than
                    // omitting the property, as known_merchant_id does on the no-hints path):
                    // wallet_id is a root property that always exists on this tool, so strict mode's
                    // "every declared property is required" would otherwise force a property that
                    // sometimes isn't there - the enum instead narrows to [null], which is what the
                    // API's schema subset offers for "this value can only ever be null".
                    ["type"] = new JsonArray("string", "null"),
                    ["enum"] = new JsonArray([.. wallets.Select(w => (JsonNode)w.Id.ToString()), null]),
                    ["description"] = WalletIdDescription,
                },
                ["balance_amount"] = new JsonObject
                {
                    ["type"] = new JsonArray("number", "null"),
                    ["description"] = BalanceAmountDescription,
                },
                ["balance_currency"] = new JsonObject
                {
                    ["type"] = new JsonArray("string", "null"),
                    ["enum"] = new JsonArray([.. CurrencyCode.Supported.Select(code => (JsonNode)code.Value), null]),
                    ["description"] = BalanceCurrencyDescription,
                },
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

    static JsonElement ToElement(JsonObject root)
    {
        using var document = JsonDocument.Parse(root.ToJsonString());
        return document.RootElement.Clone();
    }
}
