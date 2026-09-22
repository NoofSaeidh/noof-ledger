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
    static readonly string[] CurrencyCodes =
    [
        CurrencyCode.Eur.Value,
        CurrencyCode.Rsd.Value,
        CurrencyCode.Usd.Value,
        CurrencyCode.Rub.Value,
        CurrencyCode.Kzt.Value,
    ];

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
            new("amount_quote", new JsonObject
            {
                ["type"] = "string",
                ["description"] = "The amount copied from the message character for character, exactly as written. Do not convert digits, do not add or remove separators, do not add a currency symbol, and never compute or sum anything. If the message does not state an amount for this line, do not produce the line.",
            }),
            new("currency", new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(CurrencyCodes.Select(code => (JsonNode)code).ToArray()),
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
                ["type"] = "string",
                ["enum"] = new JsonArray(merchantHints.Select(m => (JsonNode)m.Id.ToString()).ToArray()),
                ["description"] = "Set this only if the merchant in the message is one of the listed known merchants.",
            }));
        }

        properties.Add(new("merchant_quote", new JsonObject
        {
            ["type"] = "string",
            ["description"] = "The merchant name copied from the message character for character. Only when a merchant is actually named and it is not one of the known merchants.",
        }));

        var lineItem = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("description", "amount_quote", "category_slug"),
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
