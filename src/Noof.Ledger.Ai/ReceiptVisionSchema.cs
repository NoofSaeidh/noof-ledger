using System.Text.Json;
using System.Text.Json.Nodes;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Ai;

// Raw JSON Schema for read_receipt, built the same way CategorizationSchema builds
// record_transaction: a JsonObject tree so every level can carry "additionalProperties": false and
// every declared property can be listed in "required" (strict mode).
internal static class ReceiptVisionSchema
{
    public static JsonElement BuildReadReceipt()
    {
        var line = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("name", "quantity", "unit_price", "total"),
            ["properties"] = new JsonObject
            {
                ["name"] = new JsonObject { ["type"] = "string", ["description"] = "The line's name, as printed." },
                ["quantity"] = new JsonObject { ["type"] = "number", ["description"] = "The quantity, as printed." },
                ["unit_price"] = new JsonObject { ["type"] = "number", ["description"] = "The unit price, as printed." },
                ["total"] = new JsonObject { ["type"] = "number", ["description"] = "The line's total, as printed." },
            },
        };

        var root = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray(
                "seller_name", "seller_tax_id", "issued_at", "currency", "total", "payment_method", "kind", "lines"),
            ["properties"] = new JsonObject
            {
                ["seller_name"] = new JsonObject
                {
                    ["type"] = new JsonArray("string", "null"),
                    ["description"] = "The seller's business name as printed, or null when not legible.",
                },
                ["seller_tax_id"] = new JsonObject
                {
                    ["type"] = new JsonArray("string", "null"),
                    ["description"] = "The seller's tax id (PIB), if printed and legible; otherwise null.",
                },
                ["issued_at"] = new JsonObject
                {
                    ["type"] = new JsonArray("string", "null"),
                    ["description"] =
                        "The date and time printed on the receipt, as a local ISO date-time "
                        + "(YYYY-MM-DDTHH:mm:ss), or null when not legible.",
                },
                ["currency"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray([.. CurrencyCode.Supported.Select(code => (JsonNode)code.Value)]),
                    ["description"] = "The receipt's currency. Assume RSD unless the receipt clearly states another.",
                },
                ["total"] = new JsonObject { ["type"] = "number", ["description"] = "The receipt's total, as printed." },
                ["payment_method"] = NullableEnum.String(
                    ["card", "cash", "transfer", "voucher", "other", "mixed"],
                    "How the receipt says it was paid, or null when not stated."),
                ["kind"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("sale", "refund"),
                    ["description"] = "Whether the receipt is a sale or a refund.",
                },
                ["lines"] = new JsonObject
                {
                    ["type"] = "array",
                    ["minItems"] = 0,
                    ["items"] = line,
                    ["description"] = "The receipt's line items, in the order printed.",
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
