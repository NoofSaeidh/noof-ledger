using Noof.Ledger.Application.Categorization;

namespace Noof.Ledger.Application.Wallets;

public interface IWalletDirectory
{
    // Every wallet that is not archived, ordered by name: what capture may record into (M3, M4).
    Task<IReadOnlyList<WalletOption>> ActiveAsync(CancellationToken cancellationToken);
}
