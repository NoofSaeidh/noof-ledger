using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Wallets;

namespace Noof.Ledger.Persistence.Wallets;

internal sealed class EfWalletDirectory(LedgerDbContext db) : IWalletDirectory
{
    public async Task<IReadOnlyList<WalletOption>> ActiveAsync(CancellationToken cancellationToken)
    {
        var wallets = await db.Wallets.AsNoTracking()
            .Where(wallet => !wallet.Archived)
            .OrderBy(wallet => wallet.Name)
            .ThenBy(wallet => wallet.Id)
            .ToListAsync(cancellationToken);

        return [.. wallets.Select(wallet => new WalletOption(
            wallet.Id, wallet.Name, wallet.Currency, wallet.Aliases, wallet.IsDefaultForCurrency))];
    }
}
