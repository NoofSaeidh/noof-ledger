using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Wallets;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Wallets;

// Every write is one set-based statement, as in EfWalletAdmin: the page's context lives as long as its circuit, and a
// row added through the change tracker would stay tracked after a set-based delete, so adding that currency back on
// the same context would throw.
internal sealed class EfWalletFxTerms(LedgerDbContext db) : IWalletFxTerms
{
    public async Task<IReadOnlyList<WalletTermsDetails>> ListAsync(Guid walletId, CancellationToken cancellationToken)
    {
        var terms = await db.WalletFxTerms.AsNoTracking()
            .Where(t => t.WalletId == walletId)
            .ToListAsync(cancellationToken);

        return
        [
            .. terms
                .OrderBy(t => t.Currency.Value, StringComparer.Ordinal)
                .Select(t => new WalletTermsDetails(t.Currency, t.Rate, t.FeePercent, t.FeeFixed, t.FeeMinimum)),
        ];
    }

    public async Task SetAsync(Guid walletId, WalletTermsDetails terms, CancellationToken cancellationToken)
    {
        RequireInRange(terms);

        var wallet = await db.Wallets.AsNoTracking()
            .Where(w => w.Id == walletId)
            .Select(w => new { w.Currency })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"There is no wallet {walletId}.");

        if (terms.Currency == wallet.Currency)
            throw new ArgumentException(
                $"Wallet {walletId} holds {wallet.Currency}; its terms are for spending in another currency.", nameof(terms));

        var currency = terms.Currency.Value;

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO wallet_fx_terms (wallet_id, currency, rate, fee_percent, fee_fixed, fee_minimum)
            VALUES ({walletId}, {currency}, {terms.Rate}, {terms.FeePercent}, {terms.FeeFixed}, {terms.FeeMinimum})
            ON CONFLICT (wallet_id, currency) DO UPDATE SET
                rate = EXCLUDED.rate,
                fee_percent = EXCLUDED.fee_percent,
                fee_fixed = EXCLUDED.fee_fixed,
                fee_minimum = EXCLUDED.fee_minimum
            """,
            cancellationToken);
    }

    public Task RemoveAsync(Guid walletId, CurrencyCode currency, CancellationToken cancellationToken) =>
        db.WalletFxTerms
            .Where(t => t.WalletId == walletId && t.Currency == currency)
            .ExecuteDeleteAsync(cancellationToken);

    static void RequireInRange(WalletTermsDetails terms)
    {
        if (terms.Rate <= 0m)
            throw new ArgumentOutOfRangeException(
                nameof(terms), terms.Rate, $"The {terms.Currency} rate must be above zero.");

        if (terms.FeePercent < 0m || terms.FeeFixed < 0m || terms.FeeMinimum < 0m)
            throw new ArgumentOutOfRangeException(
                nameof(terms), $"A {terms.Currency} fee cannot be negative.");
    }
}
