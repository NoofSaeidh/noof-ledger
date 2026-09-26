using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Wallets;
using Noof.Ledger.Domain;

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

    public async Task<Guid?> DefaultForPaymentAsync(PaymentMethod method, CancellationToken cancellationToken)
    {
        var domainMethod = method switch
        {
            PaymentMethod.Card => WalletPaymentDefault.Card,
            PaymentMethod.Cash => WalletPaymentDefault.Cash,
            _ => (WalletPaymentDefault?)null,
        };

        if (domainMethod is null)
            return null;

        return await db.Wallets.AsNoTracking()
            .Where(wallet => wallet.DefaultForPayment == domainMethod)
            .Select(wallet => (Guid?)wallet.Id)
            .SingleOrDefaultAsync(cancellationToken);
    }
}
