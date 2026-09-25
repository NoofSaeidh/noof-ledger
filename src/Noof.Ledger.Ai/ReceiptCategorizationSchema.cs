using System.Text.Json;
using System.Text.Json.Nodes;
using Noof.Ledger.Application.Categorization;

namespace Noof.Ledger.Ai;

internal static class ReceiptCategorizationSchema
{
    const string WalletIdDescription = "The id of the wallet the caption names, or null when it names none.";

    public static JsonElement BuildCategorizeReceipt(IReadOnlyList<CategoryOption> categories, IReadOnlyList<WalletOption> wallets)
    {
        var line = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("ordinal", "category_slug"),
            ["properties"] = new JsonObject
            {
                ["ordinal"] = new JsonObject { ["type"] = "integer", ["description"] = "The line's ordinal, as given." },
                ["category_slug"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray([.. categories.Select(category => (JsonNode)category.Slug)]),
                },
            },
        };

        var root = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("lines", "merchant_name", "wallet_id"),
            ["properties"] = new JsonObject
            {
                ["lines"] = new JsonObject
                {
                    ["type"] = "array",
                    ["minItems"] = 0,
                    ["items"] = line,
                    ["description"] = "One entry per line you were given, each with the category that fits it.",
                },
                ["merchant_name"] = new JsonObject
                {
                    ["type"] = new JsonArray("string", "null"),
                    ["description"] =
                        "The canonical shop name to store, when the merchant is not already known; "
                        + "null when it is already known.",
                },
                ["wallet_id"] = wallets.Count > 0
                    ? NullableEnum.String(wallets.Select(wallet => wallet.Id.ToString()), WalletIdDescription)
                    : new JsonObject { ["type"] = "null", ["description"] = WalletIdDescription },
            },
        };

        return ToElement(root);
    }

    static JsonElement ToElement(JsonObject root)
    {
        using var document = JsonDocument.Parse(root.ToJsonString());
        return document.RootElement.Clone();
    }
}
