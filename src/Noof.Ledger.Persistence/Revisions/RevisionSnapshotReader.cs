using System.Globalization;
using System.Text.Json;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Revisions;

// Reads back what RevisionLog wrote, in every shape it has written: a snapshot from before Phase 4 has no "kind" and
// no "wallet_id", one from before Phase 7 has no item "role", no "transfer" and no "charges", and a revision seeded
// with "{}" has nothing at all. A key added later, or a JSON null, reads as absent; a key the writer always writes
// inside a block is required, so a damaged snapshot fails loudly instead of reading as zero.
internal static class RevisionSnapshotReader
{
    public static ParsedSnapshot? Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !(root.TryGetProperty("kind", out _) || root.TryGetProperty("items", out _)))
            return null;

        return new ParsedSnapshot(
            OptionalString(root, "kind") is { } name && Enum.TryParse<TransactionKind>(name, out var kind) ? kind : null,
            OptionalGuid(root, "wallet_id"),
            [.. Elements(root, "items").Select(Item)],
            Present(root, "transfer") is { } transfer ? Transfer(transfer) : null,
            [.. Elements(root, "charges").Select(Charge)]);
    }

    static ParsedItem Item(JsonElement item) => new(
        Text(item, "description"),
        new Money(Amount(item, "amount"), new CurrencyCode(Text(item, "currency"))),
        OptionalString(item, "category_slug"),
        Present(item, "role") is { } role ? (EntryRole)role.GetInt32() : EntryRole.Principal);

    static ParsedTransfer Transfer(JsonElement transfer) => new(
        transfer.GetProperty("from_wallet_id").GetGuid(),
        new Money(Amount(transfer, "from_amount"), new CurrencyCode(Text(transfer, "from_currency"))),
        transfer.GetProperty("to_wallet_id").GetGuid(),
        new Money(Amount(transfer, "to_amount"), new CurrencyCode(Text(transfer, "to_currency"))),
        Present(transfer, "fee_leg") is { } leg ? (TransferLeg)leg.GetInt32() : null,
        OptionalString(transfer, "stated_rate") is { } rate ? Parse(rate) : null,
        OptionalString(transfer, "stated_rate_base") is { } rateBase ? new CurrencyCode(rateBase) : null,
        OptionalGuid(transfer, "venue_merchant_id"));

    static ParsedCharge Charge(JsonElement charge) => new(
        new CurrencyCode(Text(charge, "currency")),
        Amount(charge, "charged_amount"),
        Amount(charge, "fee_amount"),
        Amount(charge, "rate_used"),
        OptionalString(charge, "fee_percent") is { } percent ? Parse(percent) : null,
        OptionalString(charge, "fee_fixed") is { } fixedFee ? Parse(fixedFee) : null,
        OptionalString(charge, "fee_minimum") is { } minimum ? Parse(minimum) : null,
        (ChargeSource)charge.GetProperty("source").GetInt32());

    static JsonElement? Present(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

    static IEnumerable<JsonElement> Elements(JsonElement element, string name) =>
        Present(element, name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

    static string? OptionalString(JsonElement element, string name) => Present(element, name)?.GetString();

    static Guid? OptionalGuid(JsonElement element, string name) => Present(element, name)?.GetGuid();

    static string Text(JsonElement element, string name) =>
        element.GetProperty(name).GetString() ?? throw new JsonException($"Snapshot field '{name}' is null.");

    // Decimal strings, never JSON numbers (RevisionLog), so nothing here passes through floating point.
    static decimal Amount(JsonElement element, string name) => Parse(Text(element, name));

    static decimal Parse(string text) =>
        decimal.Parse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
}
