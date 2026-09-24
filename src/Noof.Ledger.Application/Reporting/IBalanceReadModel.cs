using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Reporting;

public sealed record WalletBalance(
    Guid WalletId,
    string WalletName,
    CurrencyCode WalletCurrency,
    bool Archived,
    IReadOnlyList<Money> Balances,
    DateOnly? LastCheckedOn);

// Balances are derived when asked, never stored. Wallets come active first, then by name; a wallet nothing has counted
// for yet has no lines. Lines come in the wallet's own currency first, then the others by code.
public interface IBalanceReadModel
{
    Task<IReadOnlyList<WalletBalance>> BalancesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<Money>> BalanceOfAsync(Guid walletId, CancellationToken cancellationToken);
}
