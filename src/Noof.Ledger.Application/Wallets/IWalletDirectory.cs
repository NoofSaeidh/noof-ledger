using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Wallets;

public interface IWalletDirectory
{
    // Every wallet that is not archived, ordered by name: what capture may record into (M3, M4).
    Task<IReadOnlyList<WalletOption>> ActiveAsync(CancellationToken cancellationToken);

    // The wallet marked default for this payment method in this currency (R-3, T-13): "Cash RSD" and "Cash EUR"
    // can both be the Cash default. Only Card and Cash can ever have one - every other PaymentMethod answers null
    // without a query. An archived wallet is never the answer.
    Task<Guid?> DefaultForPaymentAsync(PaymentMethod method, CurrencyCode currency, CancellationToken cancellationToken);
}
