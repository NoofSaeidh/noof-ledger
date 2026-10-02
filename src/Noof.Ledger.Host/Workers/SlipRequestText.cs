using System.Globalization;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Host.Workers;

// A correction of a slip's exchange reads the slip's own evidence as "the message" (spec A-12), so the caption reaches
// the model once. A printed commission sits inside the amount of its side; it is shown the way the current record
// shows a fee (A-21) - the given side without it, the fee beside it "not included in the figure"; the received side as
// paid out, the fee "already taken out of the figure" - so the slip text and the current record agree and either
// answer settles to the slip's own amounts rather than charging the fee twice. C# does the sum.
internal static class SlipRequestText
{
    const string NotRead = "not read";

    sealed record SideFee(TransferLeg Leg, decimal Amount, CurrencyCode Currency);

    public static string For(SlipFacts slip, string? note)
    {
        var evidence = slip.Evidence;
        var fee = FeeBesideASide(evidence);
        List<string> lines =
        [
            "A currency exchange at an exchange office, read from a photo of its slip. Both sides were cash.",
            "Each side is the money handed over or received, as a person would say it. A commission the slip printed inside a side's amount is shown beside that side as a fee - not included in a figure handed over, already taken out of a figure received - or else on a line of its own.",
            $"Office: {(string.IsNullOrWhiteSpace(slip.VenueName) ? NotRead : slip.VenueName)}",
            $"Given: {Side(evidence.GivenAmount, evidence.GivenCurrency, TransferLeg.From, fee)}",
            $"Received: {Side(evidence.ReceivedAmount, evidence.ReceivedCurrency, TransferLeg.To, fee)}",
            $"Rate printed on the slip (dinars per one unit of the foreign currency): {Rate(evidence.Rate)}",
        ];

        if (fee is null)
            lines.Add($"Commission: {Commission(evidence)}");

        if (!string.IsNullOrWhiteSpace(note))
            lines.Add($"The operator's note on the photo: {note.Trim()}");

        return string.Join('\n', lines);
    }

    // The commission sits on the leg RecordExchange stored it on - one rule, or the two would put the fee on different
    // sides and charge it twice. It is shown beside its side only when that side's amount was read and stays positive
    // without it.
    static SideFee? FeeBesideASide(ExtractedExchange evidence)
    {
        if (evidence.CommissionAmount is not { } commission || commission <= 0m
            || evidence.CommissionCurrencyOrDinars() is not { } currency)
            return null;

        return ExchangeSlipMapper.CommissionLegOf(evidence) switch
        {
            TransferLeg.From when evidence.GivenAmount > commission => new SideFee(TransferLeg.From, commission, currency),
            TransferLeg.To when evidence.ReceivedAmount > 0m => new SideFee(TransferLeg.To, commission, currency),
            _ => null,
        };
    }

    // The customer handed over the given side with the fee inside it, and got back the received side with the fee
    // already out of it: so the given side is shown without the commission and the received side as printed.
    static string Side(decimal? amount, string? currency, TransferLeg leg, SideFee? fee) => (amount, fee) switch
    {
        ({ } printed, { Leg: TransferLeg.From } beside) when leg == TransferLeg.From =>
            $"{Figure(printed - beside.Amount, beside.Currency.Value)}, "
            + $"plus a fee of {Figure(beside.Amount, beside.Currency.Value)} on this side (not included in the figure)",
        ({ } printed, { Leg: TransferLeg.To } beside) when leg == TransferLeg.To =>
            $"{Figure(printed, beside.Currency.Value)}, "
            + $"after a fee of {Figure(beside.Amount, beside.Currency.Value)} on this side (already taken out of the figure)",
        _ => Figure(amount, currency),
    };

    // The amount and the currency are read independently, so each is shown or marked unread on its own: a side whose
    // amount is unread still tells the model which currency the reply's bare number is in. numeric(19,4) reads back
    // as 100.0000; the model is shown money as money, without losing a fourth decimal.
    static string Figure(decimal? amount, string? currency) =>
        (amount, string.IsNullOrWhiteSpace(currency) ? null : currency.Trim().ToUpperInvariant()) switch
        {
            (null, null) => NotRead,
            ({ } value, var code) => $"{value.ToString("0.00##", CultureInfo.InvariantCulture)} {code ?? "(currency not read)"}",
            (null, { } code) => $"(amount not read) {code}",
        };

    // numeric(24,12) reads back with twelve decimals: the four a slip prints, and any further ones that mean something.
    static string Rate(decimal? rate) => rate?.ToString("0.0000########", CultureInfo.InvariantCulture) ?? NotRead;

    // A commission printed with no currency is dinars, as ExtractedExchange reads it.
    static string Commission(ExtractedExchange evidence) => evidence.CommissionAmount is { } value && value > 0m
        ? $"{Figure(value, evidence.CommissionCurrencyOrDinars()?.Value ?? evidence.CommissionCurrency)} (printed inside the amount on its side)"
        : NotRead;
}
