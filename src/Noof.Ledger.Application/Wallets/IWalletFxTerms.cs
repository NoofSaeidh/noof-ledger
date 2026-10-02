using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Wallets;

// One wallet's terms for spending in one foreign currency (T-6): Rate is wallet-currency units per one Currency,
// FeePercent 1.5 means 1.5 %, FeeFixed and FeeMinimum are in the wallet's currency.
public sealed record WalletTermsDetails(CurrencyCode Currency, decimal Rate, decimal? FeePercent, decimal? FeeFixed, decimal? FeeMinimum);

// Edited on /wallets. As with IWalletAdmin, a caller that lets a bad value through has a bug, so SetAsync writes
// nothing and throws: an unknown wallet KeyNotFoundException, the wallet's own currency ArgumentException, a rate
// that is not positive or a negative fee term ArgumentOutOfRangeException.
public interface IWalletFxTerms
{
    // Ordered by currency code, ordinal.
    Task<IReadOnlyList<WalletTermsDetails>> ListAsync(Guid walletId, CancellationToken cancellationToken);

    // Inserts the terms for terms.Currency, or replaces them.
    Task SetAsync(Guid walletId, WalletTermsDetails terms, CancellationToken cancellationToken);

    // Terms that do not exist are a no-op.
    Task RemoveAsync(Guid walletId, CurrencyCode currency, CancellationToken cancellationToken);
}
