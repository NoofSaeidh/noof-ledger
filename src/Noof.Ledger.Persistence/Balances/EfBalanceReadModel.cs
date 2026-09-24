using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Reporting;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Balances;

internal sealed class EfBalanceReadModel(LedgerDbContext db) : IBalanceReadModel
{
    public async Task<IReadOnlyList<WalletBalance>> BalancesAsync(CancellationToken cancellationToken)
    {
        var wallets = await db.Wallets.AsNoTracking().ToListAsync(cancellationToken);
        var rowsByWallet = (await Rows().ToListAsync(cancellationToken)).ToLookup(row => row.WalletId);

        return
        [
            .. wallets
                .OrderBy(wallet => wallet.Archived)
                .ThenBy(wallet => wallet.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(wallet => wallet.Id)
                .Select(wallet => new WalletBalance(
                    wallet.Id,
                    wallet.Name,
                    wallet.Currency,
                    wallet.Archived,
                    Ordered(rowsByWallet[wallet.Id], wallet.Currency),
                    rowsByWallet[wallet.Id].Max(row => row.CheckedOn))),
        ];
    }

    public async Task<IReadOnlyList<Money>> BalanceOfAsync(Guid walletId, CancellationToken cancellationToken)
    {
        var wallet = await db.Wallets.AsNoTracking().SingleOrDefaultAsync(w => w.Id == walletId, cancellationToken);
        if (wallet is null)
            return [];

        var rows = await Rows().Where(row => row.WalletId == walletId).ToListAsync(cancellationToken);
        return Ordered(rows, wallet.Currency);
    }

    // A raw query onto a type the model does not map, so the view never enters the EF model: it cannot show up in
    // the snapshot, in schema.expected.sql or in the next migration's diff.
    IQueryable<WalletBalanceRow> Rows() => db.Database.SqlQueryRaw<WalletBalanceRow>(
        """
        SELECT wallet_id AS "WalletId", currency AS "Currency", balance AS "Balance", checked_on AS "CheckedOn"
        FROM public.wallet_balances
        """);

    static IReadOnlyList<Money> Ordered(IEnumerable<WalletBalanceRow> rows, CurrencyCode walletCurrency) =>
    [
        .. rows
            .Select(row => new Money(row.Balance, new CurrencyCode(row.Currency)))
            .OrderBy(balance => balance.Currency == walletCurrency ? 0 : 1)
            .ThenBy(balance => balance.Currency),
    ];
}

internal sealed class WalletBalanceRow
{
    public Guid WalletId { get; init; }
    public string Currency { get; init; } = string.Empty;
    public decimal Balance { get; init; }
    public DateOnly? CheckedOn { get; init; }
}
