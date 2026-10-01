namespace Noof.Ledger.Domain;

// A wallet's terms for spending in one foreign currency (T-6). Rate is wallet-currency units per one Currency;
// FeePercent 1.5 means 1.5 %; FeeFixed and FeeMinimum are in the wallet's currency. Currency is never the wallet's
// own: the store refuses it, since a check constraint cannot see the wallet's row.
public sealed class WalletFxTerms
{
    public required Guid WalletId { get; init; }
    public required CurrencyCode Currency { get; init; }
    public required decimal Rate { get; set; }
    public decimal? FeePercent { get; set; }
    public decimal? FeeFixed { get; set; }
    public decimal? FeeMinimum { get; set; }
}
