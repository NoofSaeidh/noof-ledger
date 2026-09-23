using System.Text.Json;
using System.Text.Json.Nodes;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Ai;

// Raw JSON Schema, built as a JsonElement rather than through a typed builder or reflection:
// ChatResponseFormat.ForJsonSchema and a raw-schema AIFunctionDeclaration both need
// "additionalProperties": false at every object level, which is easiest to guarantee by building
// the JsonObject tree directly and controlling every key by hand.
internal static class CategorizationSchema
{
    const string AmountDescription =
        "The amount the person meant, as a number - for example 1000 or 45.3. Interpret words, slang and speech: "
        + "\"штуку\" is 1000, \"полтос\" is 50, \"двести пятьдесят\" is 250.";

    public static JsonElement BuildRecordSpending(
        IReadOnlyList<CategoryOption> categories, IReadOnlyList<MerchantOption> merchantHints)
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
            ["required"] = new JsonArray("items"),
            ["properties"] = new JsonObject
            {
                ["items"] = new JsonObject
                {
                    ["type"] = "array",
                    // 0, not 1: a message can genuinely describe zero purchases (a loan received,
                    // not a purchase - see CategorizationPrompt's "заняла у Маши" example). Only 0
                    // and 1 are valid values for minItems under this API's schema subset, and
                    // requiring at least one item here would force the model to invent a spend it
                    // was just told not to record.
                    ["minItems"] = 0,
                    ["items"] = lineItem,
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
