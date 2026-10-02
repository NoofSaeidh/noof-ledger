using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Revisions;

namespace Noof.Ledger.Persistence.Categorization;

// The one build for the charges stored today and those a revision snapshot kept, so the categorisation subject, the
// trace summary and the trace history cannot read a charge differently.
internal static class ChargeViews
{
    public static ChargeView Of(Charge charge, IEnumerable<(EntryRole Role, Money Amount)> lines, CurrencyCode walletCurrency) =>
        Of(
            new ParsedCharge(
                charge.Currency, charge.ChargedAmount, charge.FeeAmount, charge.RateUsed, charge.FeePercent,
                charge.FeeFixed, charge.FeeMinimum, charge.Source),
            lines,
            walletCurrency);

    public static ChargeView Of(ParsedCharge charge, IEnumerable<(EntryRole Role, Money Amount)> lines, CurrencyCode walletCurrency) =>
        new(
            charge.Currency,
            lines
                .Where(line => line.Role == EntryRole.Principal && line.Amount.Currency == charge.Currency)
                .Sum(line => line.Amount.Amount),
            new Money(charge.ChargedAmount, walletCurrency),
            new Money(charge.FeeAmount, walletCurrency),
            charge.RateUsed,
            new FeeTerms(charge.FeePercent, charge.FeeFixed, charge.FeeMinimum),
            charge.Source);
}
