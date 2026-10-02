using System.Globalization;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Reporting;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Web;

// One wording of a transfer and of a charge for every page that shows one, so Home, /transactions and the trace page
// cannot describe the same transfer three ways. Amounts keep the pages' N2 invariant format.
internal static class LedgerText
{
    public static string Amount(Money money) => $"{money.Amount.ToString("N2", CultureInfo.InvariantCulture)} {money.Currency}";

    public static string TransferLegs(TransferLine line) =>
        $"{line.FromWalletName} → {line.ToWalletName} · {Amount(line.From)} → {Amount(line.To)}";

    public static string TransferWithRate(TransferLine line) =>
        line.Rate is { } rate ? $"{TransferLegs(line)} · {rate}" : TransferLegs(line);

    public static (string From, string To) SignedTransferLegs(TransferLine line) => ($"-{Amount(line.From)}", $"→ +{Amount(line.To)}");

    // The one test of "was a fee taken": a charge without one stores 0 (charges.fee_amount), and "+ fee 0.00" would
    // read as a fee that was taken.
    public static bool IsFee(Money? fee) => fee is { Amount: > 0m };

    public static string? TransferFee(TransferLine line) => TransferFeeTaken(line) is { } taken ? $"Fee {taken}" : null;

    // For a row already labelled Fee.
    public static string? TransferFeeTaken(TransferLine line) =>
        line.Fee is { } fee && IsFee(fee)
            ? $"{Amount(fee)} from {(line.FeeLeg == TransferLeg.To ? line.ToWalletName : line.FromWalletName)}"
            : null;

    public static string Charge(ChargeView charge)
    {
        var rate = new ExchangeRate(charge.Currency, charge.RateUsed, charge.Charged.Currency);
        var source = charge.Source == ChargeSource.Stated ? "stated" : "wallet rate";
        var fee = IsFee(charge.Fee) ? $" + fee {Amount(charge.Fee)}" : string.Empty;
        return $"{charge.ForeignSum.ToString("N2", CultureInfo.InvariantCulture)} {charge.Currency} → charged {Amount(charge.Charged)} "
            + $"({rate}, {source}){fee}{Terms(charge.Terms)}";
    }

    static string Terms(FeeTerms terms)
    {
        var parts = new List<string>();
        if (terms.Percent is { } percent)
            parts.Add($"{percent.ToString("0.####", CultureInfo.InvariantCulture)} %");
        if (terms.Fixed is { } fixedFee)
            parts.Add($"fixed {fixedFee.ToString("N2", CultureInfo.InvariantCulture)}");
        if (terms.Minimum is { } minimum)
            parts.Add($"minimum {minimum.ToString("N2", CultureInfo.InvariantCulture)}");

        return parts.Count == 0 ? string.Empty : $" · terms {string.Join(", ", parts)}";
    }
}
