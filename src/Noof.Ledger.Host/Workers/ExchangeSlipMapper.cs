using System.Diagnostics.CodeAnalysis;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Host.Workers;

// A slip's evidence as the transfer it describes, without the model (spec §3, Recording 2). From the customer's
// side: what they gave is the source leg, what they got back the destination, and a printed commission is a fee
// inside the amount of the leg whose currency it is in. Currencies, the rate and the commission's currency are read
// through ExtractedExchange's own rules; TransferRequest.TrySettle does every sum, including filling a missing
// received amount from the printed rate. ExchangeSlipMapperTests pins the amount checks here to Assess.
internal static class ExchangeSlipMapper
{
    public static bool TryRead(
        ExtractedExchange slip, [NotNullWhen(true)] out TransferRequest? request, out RecordFailureReason failure)
    {
        request = null;
        failure = RecordFailureReason.SlipIncomplete;

        if (slip.GivenAmount is not { } given || given <= 0m
            || ExtractedExchange.SupportedCurrency(slip.GivenCurrency) is not { } givenCurrency
            || ExtractedExchange.SupportedCurrency(slip.ReceivedCurrency) is not { } receivedCurrency)
            return false;

        if (!TryReadFee(slip, givenCurrency, receivedCurrency, out var fee))
        {
            failure = RecordFailureReason.InvalidFee;
            return false;
        }

        var received = slip.ReceivedAmount is > 0m ? slip.ReceivedAmount : null;
        request = new TransferRequest(new Money(given, givenCurrency), receivedCurrency, received, slip.PrintedRate(), fee);
        return true;
    }

    // The office is the venue, keyed by its PIB as a shop is (spec §3, A-9); a slip whose PIB was not read has none.
    public static async Task<Guid?> VenueOfAsync(ExchangeSlipView slip, IMerchantDirectory merchants, CancellationToken cancellationToken) =>
        slip.SellerTaxId is { Length: > 0 } taxId
            ? await merchants.VenueForTaxIdAsync(taxId, slip.SellerName is { Length: > 0 } name ? name : taxId, cancellationToken)
            : null;

    // A slip is always cash on both legs (spec §3, T-13, A-8): the cash default of the leg's currency, else that
    // currency's default. Not capture's rule (A-27 puts an unnamed source on the card), so a reply that completes a
    // slip takes the same rule (spec A-8).
    public static Guid? CashWalletOf(IReadOnlyList<WalletOption> wallets, CurrencyCode currency) =>
        (wallets.FirstOrDefault(wallet => wallet.Currency == currency && wallet.DefaultForPayment == WalletPaymentDefault.Cash)
         ?? wallets.FirstOrDefault(wallet => wallet.Currency == currency && wallet.IsDefaultForCurrency))?.Id;

    static bool TryReadFee(ExtractedExchange slip, CurrencyCode given, CurrencyCode received, out TransferFeeRequest? fee)
    {
        fee = null;
        if (slip.CommissionAmount is not { } commission || commission <= 0m)
            return true;

        if (slip.CommissionCurrencyOrDinars() is not { } currency)
            return false;

        TransferLeg? leg = currency == given ? TransferLeg.From : currency == received ? TransferLeg.To : null;
        if (leg is not { } feeLeg)
            return false;

        fee = new TransferFeeRequest(new Money(commission, currency), feeLeg, Included: true);
        return true;
    }
}
