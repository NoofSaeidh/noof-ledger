using System.Diagnostics.CodeAnalysis;

namespace Noof.Ledger.Domain;

// A transfer as the operator said it, settled into what each wallet moved (spec §2, rules 2-3). The model only copies
// the figures; every addition, subtraction and conversion happens here.
public sealed record TransferRequest(Money From, CurrencyCode ToCurrency, decimal? ToAmount, ExchangeRate? Rate, TransferFeeRequest? Fee)
{
    // The stated rate worth keeping on the record: one that converts between the two legs, whether or not the settlement
    // needed it beside a said received amount.
    public ExchangeRate? ConvertingRate => Rate is { } rate && From.Currency != ToCurrency && Converts(rate) ? rate : null;

    public bool TrySettle([MaybeNullWhen(false)] out SettledTransfer settled, out RecordFailureReason failure)
    {
        settled = null;

        // A fee of zero leaves no fee line, so it has no leg either.
        var fee = Fee is { Amount.Amount: 0m } ? null : Fee;

        if (From.Amount <= 0m || ToAmount <= 0m)
            return Fails(RecordFailureReason.InvalidAmount, out failure);

        if (fee is not null && (fee.Amount.Amount < 0m || fee.Amount.Currency != CurrencyOf(fee.Leg)))
            return Fails(RecordFailureReason.InvalidFee, out failure);

        var sourcePrincipal = fee is { Leg: TransferLeg.From, Included: true } ? From - fee.Amount : From;
        if (sourcePrincipal.Amount <= 0m)
            return Fails(RecordFailureReason.InvalidFee, out failure);

        if (DestinationPrincipal(sourcePrincipal, fee, out failure) is not { } destinationPrincipal)
            return false;

        if (destinationPrincipal.Amount <= 0m)
            return Fails(RecordFailureReason.InvalidAmount, out failure);

        var storedFrom = fee is { Leg: TransferLeg.From } ? sourcePrincipal + fee.Amount : sourcePrincipal;
        var storedTo = fee is { Leg: TransferLeg.To } ? destinationPrincipal - fee.Amount : destinationPrincipal;
        if (storedTo.Amount <= 0m)
            return Fails(RecordFailureReason.InvalidFee, out failure);

        settled = new SettledTransfer(storedFrom, storedTo, fee?.Amount, fee?.Leg);
        failure = RecordFailureReason.None;
        return true;
    }

    // Rule 2, first that applies: the amount said for the destination; the same currency; a rate; nothing.
    Money? DestinationPrincipal(Money sourcePrincipal, TransferFeeRequest? fee, out RecordFailureReason failure)
    {
        failure = RecordFailureReason.None;

        if (ToAmount is { } said)
        {
            var received = new Money(said, ToCurrency);
            return fee is { Leg: TransferLeg.To, Included: true } ? received + fee.Amount : received;
        }

        if (From.Currency == ToCurrency)
            return sourcePrincipal;

        if (Rate is not { } rate)
        {
            failure = RecordFailureReason.MissingReceivedAmount;
            return null;
        }

        if (!Converts(rate))
        {
            failure = RecordFailureReason.InvalidRate;
            return null;
        }

        return rate.Convert(sourcePrincipal);
    }

    // The two legs' currencies in either direction, quoted positive. The legs differ here, so a rate whose base equals
    // its quote can never match.
    bool Converts(ExchangeRate rate) =>
        rate.QuoteAmount > 0m
        && ((rate.Base == From.Currency && rate.Quote == ToCurrency) || (rate.Base == ToCurrency && rate.Quote == From.Currency));

    CurrencyCode CurrencyOf(TransferLeg leg) => leg == TransferLeg.From ? From.Currency : ToCurrency;

    static bool Fails(RecordFailureReason reason, out RecordFailureReason failure)
    {
        failure = reason;
        return false;
    }
}
