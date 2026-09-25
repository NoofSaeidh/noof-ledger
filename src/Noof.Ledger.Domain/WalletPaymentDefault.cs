namespace Noof.Ledger.Domain;

// Only Card and Cash can be a wallet's default (R-3): a wallet is never the default for Transfer,
// Voucher, Other or Mixed, so this stays its own small enum rather than the full PaymentMethod.
public enum WalletPaymentDefault
{
    Card = 0,
    Cash = 1,
}
