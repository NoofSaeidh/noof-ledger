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
            ["required"] = new JsonArray("lines", "merchant_name", "wallet_id", "unsupported_change"),
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
                // N-8 (Phase 6 re-review): "none" carries the field's own not-applicable case, so it
                // needs no nullable/anyOf treatment - a correction can ask for at most one of a
                // different date or a different amount, and you cannot honour either.
                ["unsupported_change"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("date", "amount", "none"),
                    ["description"] =
                        "\"date\" when a correction asked for a different date than the receipt already shows, "
                        + "\"amount\" when it asked for a different amount - you cannot change either, so answer "
                        + "the rest of the correction as usual and set this accordingly; \"none\" otherwise, "
                        + "including when there is no correction.",
                },
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
