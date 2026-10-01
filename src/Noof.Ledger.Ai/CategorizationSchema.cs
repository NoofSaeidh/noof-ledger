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
        "What kind of record this is: \"expense\" for money spent, \"income\" for money received, \"balance\" "
        + "when the person states what a wallet's balance is right now rather than a purchase or a deposit, or "
        + "\"transfer\" when money moves between the person's own wallets - a withdrawal, a top-up, a transfer "
        + "between their accounts or a currency exchange. items must be empty for kind \"balance\" and kind \"transfer\".";

    const string WalletIdDescription =
        "The id of the wallet the person means, chosen from the wallets you were offered, or null when no "
        + "wallet is named or none of the offered wallets fits - the ledger then uses the default wallet "
        + "for the spending's currency.";

    const string BalanceAmountDescription =
        "The balance the person stated, as a number, when kind is \"balance\"; null for every other kind.";

    const string BalanceCurrencyDescription =
        "The currency of the stated balance, when kind is \"balance\" and the person named one; null for "
        + "every other kind, or when they named none - the wallet's own currency is used then.";

    const string TransferDescription =
        "What moved, when kind is \"transfer\": the side the money left (from_*) and the side it arrived on (to_*), "
        + "with every amount exactly as the person said it; null for every other kind.";

    const string FromWalletIdDescription =
        "The id of the wallet the money left, chosen from the wallets you were offered, or null when the person "
        + "names none - the ledger then uses the card wallet of from_currency, or else its default wallet.";

    const string ToWalletIdDescription =
        "The id of the wallet the money arrived in, chosen from the wallets you were offered, or null when the "
        + "person names none - the ledger then uses the cash wallet of to_currency, or else its default wallet.";

    const string ToAmountDescription =
        "The amount that arrived, as the person said it, or null when they did not say it - never worked out from a rate.";

    const string RateDescription =
        "The exchange rate the person stated, read as 1 base_currency = quote_amount quote_currency, or null when "
        + "they stated none.";

    const string ChargedDescription =
        "What the wallet was actually charged, in its own currency, for a purchase in another currency - only when "
        + "the person says it; null otherwise.";

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
            new("currency", NullableEnum.String(
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
            properties.Add(new("known_merchant_id", NullableEnum.String(
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
            // kind/wallet_id/balance_amount/balance_currency are flat root properties (M3, M9): one message records one
            // transaction, in one wallet, of one kind. transfer and charged are nested because each groups figures that
            // only mean something together, and each is null for every record it does not describe.
            ["required"] = new JsonArray(
                "items", "occurred_on", "kind", "wallet_id", "balance_amount", "balance_currency", "transfer", "charged"),
            ["properties"] = new JsonObject
            {
                ["items"] = new JsonObject
                {
                    ["type"] = "array",
                    // 0, not 1: every "balance" and "transfer" answer has no items at all, and a message can
                    // otherwise mention a figure with nothing to record against it. Only 0 and 1 are valid
                    // values for minItems under this API's schema subset.
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
                    ["enum"] = new JsonArray(ProposedKind.Expense, ProposedKind.Income, ProposedKind.Balance, ProposedKind.Transfer),
                    ["description"] = KindDescription,
                },
                ["wallet_id"] = WalletId(wallets, WalletIdDescription),
                ["balance_amount"] = NullableNumber(BalanceAmountDescription),
                ["balance_currency"] = NullableEnum.String(
                    CurrencyCode.Supported.Select(code => code.Value), BalanceCurrencyDescription),
                ["transfer"] = Transfer(wallets),
                ["charged"] = Charged(),
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

    // Declared even with zero wallets offered (rather than omitted, as known_merchant_id is on the no-hints path): a
    // wallet id always exists on this tool, so with nothing to choose from it narrows to a value that can only be null.
    static JsonObject WalletId(IReadOnlyList<WalletOption> wallets, string description) =>
        wallets.Count > 0
            ? NullableEnum.String(wallets.Select(wallet => wallet.Id.ToString()), description)
            : new JsonObject { ["type"] = "null", ["description"] = description };

    static JsonObject Transfer(IReadOnlyList<WalletOption> wallets) => NullableObject.Of(
        TransferDescription,
        ("from_wallet_id", WalletId(wallets, FromWalletIdDescription)),
        ("from_amount", Number("The amount that left, as the person said it - never worked out from other figures.")),
        ("from_currency", Currency("The currency of the amount that left.")),
        ("to_wallet_id", WalletId(wallets, ToWalletIdDescription)),
        ("to_amount", NullableNumber(ToAmountDescription)),
        ("to_currency", Currency("The currency the money arrived in.")),
        ("rate", NullableObject.Of(
            RateDescription,
            ("base_currency", Currency("The currency the rate is quoted per one unit of.")),
            ("quote_amount", Number("How many quote_currency units one base_currency unit is, as stated.")),
            ("quote_currency", Currency("The currency the rate is quoted in.")))),
        ("fee", NullableObject.Of(
            "The fee the person stated for this transfer, or null when they stated none.",
            ("amount", Number("The fee, as the person said it.")),
            ("currency", Currency("The fee's currency.")),
            ("leg", new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(ProposedLeg.From, ProposedLeg.To),
                ["description"] = "\"from\" unless the person says the receiving side kept the fee - then \"to\"; a fee in only one side's currency is always on that side.",
            }),
            ("included", Boolean("True only when the person says the amount they gave for that side already includes the fee.")))));

    static JsonObject Charged() => NullableObject.Of(
        ChargedDescription,
        ("amount", Number("The charged amount, as the person said it.")),
        ("currency", Currency("The wallet's currency the amount was charged in.")),
        ("fee_amount", NullableNumber("The commission the person named for that charge, or null when they named none.")),
        ("fee_included", Boolean("True only when the person says the charged amount includes that commission.")));

    static JsonObject Number(string description) => new() { ["type"] = "number", ["description"] = description };

    static JsonObject NullableNumber(string description) =>
        new() { ["type"] = new JsonArray("number", "null"), ["description"] = description };

    static JsonObject Boolean(string description) => new() { ["type"] = "boolean", ["description"] = description };

    // A fresh node per call: a JsonNode belongs to one parent, so one shared enum array could not sit in four places.
    static JsonObject Currency(string description) => new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray([.. CurrencyCode.Supported.Select(code => (JsonNode)code.Value)]),
        ["description"] = description,
    };

    static JsonElement ToElement(JsonObject root)
    {
        using var document = JsonDocument.Parse(root.ToJsonString());
        return document.RootElement.Clone();
    }
}
