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
                "readable", "unreadable_reason", "seller_name", "seller_tax_id", "fiscal_number", "issued_at",
                "currency", "total", "payment_method", "kind", "lines", "exchange"),
            ["properties"] = new JsonObject
            {
                ["readable"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] =
                        "False when the photo cannot be read reliably enough to record it - too small, blurry, "
                        + "cut off, or neither a shop receipt nor an exchange-office slip. Never guess or fill in "
                        + "a field to make an unreadable photo look readable.",
                },
                ["unreadable_reason"] = NullableEnum.String(
                    ["too_small", "blurry", "not_a_receipt", "cut_off", "other"],
                    "Why the photo could not be read, when readable is false; otherwise null. not_a_receipt means "
                    + "it is neither a shop receipt nor an exchange-office slip."),
                ["seller_name"] = NullableValue(
                    "string", "The seller's or exchange office's business name as printed, or null when not legible."),
                ["seller_tax_id"] = NullableValue(
                    "string", "The seller's or exchange office's tax id (PIB), if printed and legible; otherwise null."),
                ["fiscal_number"] = NullableValue(
                    "string",
                    "The printed fiscal receipt number (\"ПФР број рачуна\"), if legible; otherwise null. Always "
                    + "null on an exchange-office slip, whose own number goes in exchange.slip_number."),
                ["issued_at"] = NullableValue(
                    "string",
                    "The date and time printed on the receipt or slip, as a local ISO date-time "
                    + "(YYYY-MM-DDTHH:mm:ss), or null when not legible."),
                ["currency"] = NullableEnum.String(
                    CurrencyCode.Supported.Select(code => code.Value),
                    "The receipt's currency. Assume RSD unless the receipt clearly states another; null when not "
                    + "legible, and on an exchange-office slip."),
                ["total"] = NullableValue(
                    "number", "The receipt's total, as printed, or null when not legible, and on an exchange-office slip."),
                ["payment_method"] = NullableEnum.String(
                    ["card", "cash", "transfer", "voucher", "other", "mixed"],
                    "How the receipt says it was paid, or null when not stated."),
                ["kind"] = NullableEnum.String(
                    ["sale", "refund", "exchange"],
                    "Whether the document is a sale receipt, a refund receipt, or an exchange-office slip "
                    + "(exchange), or null when not legible."),
                ["lines"] = new JsonObject
                {
                    ["type"] = "array",
                    ["minItems"] = 0,
                    ["items"] = line,
                    ["description"] =
                        "The receipt's line items, in the order printed. Empty when unreadable, and on an "
                        + "exchange-office slip.",
                },
                ["exchange"] = Exchange(),
            },
        };

        return ToElement(root);
    }

    // Not nullable itself, and the commission's amount and currency share one nullable object: the API
    // allows at most 16 parameters with a union type (anyOf or a type array) across a request's strict
    // schemas (ReceiptVisionSchemaTests guards the count), and a nullable exchange with two nullable
    // commission fields would go over it. A document that is not a slip answers this with every field null.
    static JsonObject Exchange() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray(
            "given_amount", "given_currency", "received_amount", "received_currency", "rate", "commission", "slip_number"),
        ["description"] =
            "An exchange-office slip's figures, from the customer's side: what the customer handed over and what "
            + "the customer was handed. Every field null on anything but an exchange-office slip.",
        ["properties"] = new JsonObject
        {
            ["given_amount"] = NullableValue(
                "number", "The amount the customer handed over, as printed, or null when not legible."),
            ["given_currency"] = NullableValue(
                "string",
                "The three-letter code of the currency the customer handed over (RSD for dinars), or null when not legible."),
            ["received_amount"] = NullableValue(
                "number", "The amount the customer was handed, as printed, or null when not legible."),
            ["received_currency"] = NullableValue(
                "string",
                "The three-letter code of the currency the customer was handed (RSD for dinars), or null when not legible."),
            ["rate"] = NullableValue(
                "number", "The exchange rate exactly as printed, never converted or recomputed, or null when not legible."),
            ["commission"] = new JsonObject
            {
                ["anyOf"] = new JsonArray(
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["required"] = new JsonArray("amount", "currency"),
                        ["properties"] = new JsonObject
                        {
                            ["amount"] = new JsonObject { ["type"] = "number", ["description"] = "The commission's amount, as printed." },
                            ["currency"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "The three-letter code of the commission's currency (RSD for dinars).",
                            },
                        },
                    },
                    new JsonObject { ["type"] = "null" }),
                ["description"] = "The commission the slip prints, or null when it prints none or it is not legible.",
            },
            ["slip_number"] = NullableValue(
                "string", "The slip's serial number, exactly as printed, or null when not legible."),
        },
    };

    static JsonObject NullableValue(string type, string description) => new()
    {
        ["type"] = new JsonArray(type, "null"),
        ["description"] = description,
    };

    static JsonElement ToElement(JsonObject root)
    {
        using var document = JsonDocument.Parse(root.ToJsonString());
        return document.RootElement.Clone();
    }
}
