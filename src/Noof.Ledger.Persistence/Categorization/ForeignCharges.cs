using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Noof.Ledger.Persistence.Categorization;

// What a spending in a foreign currency cost its wallet (spec §1 "charges", §2 "Mapping a foreign-currency
// spending"). Rewritten whole on every apply, inside ApplyAsync's database transaction and after its lines are
// saved, so a charge prices the Principal lines the apply actually left in place - a Rule- or User-authored
// line that survived the model's delete included - never the proposal alone.
internal static class ForeignCharges
{
    // SQL rather than tracked entities, like LedgerPostings' checkpoint: a Charge still tracked from an earlier
    // apply in the same context would collide with a new instance under the same (transaction_id, currency) key.
    const string InsertSql = """
        INSERT INTO charges
            (transaction_id, currency, charged_amount, fee_amount, rate_used, fee_percent, fee_fixed, fee_minimum, source)
        VALUES
            (@transactionId, @currency, @chargedAmount, @feeAmount, @rateUsed, @feePercent, @feeFixed, @feeMinimum, @source)
        """;

    // Returns the fees the new charges cost, above zero and in currency order, for ApplyAsync to write as fee lines.
    public static async Task<IReadOnlyList<(CurrencyCode Purchase, Money Fee)>> RewriteAsync(
        LedgerDbContext db, Transaction transaction, JobKind job, Guid? walletBefore, StatedCharge? said,
        CancellationToken cancellationToken)
    {
        var previous = await db.Charges.AsNoTracking()
            .Where(charge => charge.TransactionId == transaction.Id)
            .ToListAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM charges WHERE transaction_id = @transactionId",
            [new NpgsqlParameter("transactionId", transaction.Id)],
            cancellationToken);

        // T-1, amendment 27: a fiscal receipt is never a foreign-currency spending, so its lines stay as they are.
        if (job == JobKind.CategorizeReceipt
            || transaction.Kind != TransactionKind.Expense
            || transaction.WalletId is not { } walletId)
            return [];

        var walletCurrency = await db.Wallets.AsNoTracking()
            .Where(wallet => wallet.Id == walletId)
            .Select(wallet => wallet.Currency)
            .SingleAsync(cancellationToken);
        var foreignSums = await ForeignSumsAsync(db, transaction.Id, walletCurrency, cancellationToken);
        if (foreignSums.Count == 0)
            return [];

        // A figure stated in one wallet's currency means nothing in another's, and another wallet's terms are
        // not this one's: moving the record discards every old charge, Stated or not. An edited message is read
        // again whole, like a first reading, so a charge it no longer says was not said and today's terms price it.
        var kept = job != JobKind.Reinterpret && walletBefore == walletId
            ? previous.ToDictionary(charge => charge.Currency)
            : new Dictionary<CurrencyCode, Charge>();
        var current = (await db.WalletFxTerms.AsNoTracking()
                .Where(terms => terms.WalletId == walletId)
                .ToListAsync(cancellationToken))
            .ToDictionary(
                terms => terms.Currency,
                terms => new ChargeTerms(terms.Rate, new FeeTerms(terms.FeePercent, terms.FeeFixed, terms.FeeMinimum)));

        // A said charge cannot be split across currencies, and one in a currency other than the wallet's prices
        // nothing here (amendment 14).
        var honoured = said is not null && foreignSums.Count == 1 && said.Charged.Currency == walletCurrency ? said : null;

        List<Charge> charges = [.. foreignSums
            .Select(sum => Price(
                transaction.Id, sum.Currency, sum.Amount, honoured, chargeWasSaid: said is not null,
                kept.GetValueOrDefault(sum.Currency), current.GetValueOrDefault(sum.Currency)))
            .OfType<Charge>()];

        foreach (var charge in charges)
            await InsertAsync(db, charge, cancellationToken);

        return [.. charges
            .Where(charge => charge.FeeAmount > 0)
            .Select(charge => (Purchase: charge.Currency, Fee: new Money(charge.FeeAmount, walletCurrency)))];
    }

    static async Task<IReadOnlyList<Money>> ForeignSumsAsync(
        LedgerDbContext db, Guid transactionId, CurrencyCode walletCurrency, CancellationToken cancellationToken)
    {
        var principals = await db.LineItems.AsNoTracking()
            .Where(line => line.TransactionId == transactionId && line.Role == EntryRole.Principal)
            .Select(line => line.Amount)
            .ToListAsync(cancellationToken);

        return [.. principals
            .Where(amount => amount.Currency != walletCurrency)
            .GroupBy(amount => amount.Currency)
            .OrderBy(group => group.Key.Value, StringComparer.Ordinal)
            .Select(group => new Money(group.Sum(amount => amount.Amount), group.Key))];
    }

    static Charge? Price(
        Guid transactionId, CurrencyCode currency, decimal foreignSum, StatedCharge? honoured, bool chargeWasSaid,
        Charge? kept, ChargeTerms? current)
    {
        if (honoured is not null)
        {
            // A wallet with no terms still takes a said charge, without a fee unless one was said. Stated gives
            // null for nothing positive left or a negative said fee, and the charge falls to the terms below.
            var feeTerms = current?.Fee ?? FeeTerms.None;
            if (ChargeTerms.Stated(honoured.Charged.Amount, foreignSum, honoured.FeeAmount, honoured.FeeIncluded, feeTerms) is { } stated)
                return ChargeOf(transactionId, currency, stated, feeTerms, ChargeSource.Stated);
        }

        // Amendment 22: a kept Stated charge stays when the foreign sum changes; only its rate is derived again. Its
        // stored fee goes back in as said: a fee solved out of an included figure can differ from FeeOn(Charged) by
        // a cent, and a said fee need not be FeeOn(Charged) at all, so recomputing it would move a figure stated.
        if (!chargeWasSaid && kept is { Source: ChargeSource.Stated } && SnapshotOf(kept) is var snapshot
            && ChargeTerms.Stated(kept.ChargedAmount, foreignSum, kept.FeeAmount, feeIncluded: false, snapshot.Fee) is { } restated)
            return ChargeOf(transactionId, currency, restated, snapshot.Fee, ChargeSource.Stated);

        // While the record stays on its wallet, its own snapshot prices it again, never today's terms: a
        // correction must not reprice history (spec §1).
        var terms = kept is { Source: ChargeSource.WalletTerms } ? SnapshotOf(kept) : current;
        return terms?.ChargeFor(foreignSum) is { } computed
            ? ChargeOf(transactionId, currency, computed, terms.Fee, ChargeSource.WalletTerms)
            : null;
    }

    static ChargeTerms SnapshotOf(Charge charge) =>
        new(charge.RateUsed, new FeeTerms(charge.FeePercent, charge.FeeFixed, charge.FeeMinimum));

    static Charge ChargeOf(Guid transactionId, CurrencyCode currency, ComputedCharge computed, FeeTerms terms, ChargeSource source) => new()
    {
        TransactionId = transactionId,
        Currency = currency,
        ChargedAmount = computed.Charged,
        FeeAmount = computed.Fee,
        RateUsed = computed.RateUsed,
        FeePercent = terms.Percent,
        FeeFixed = terms.Fixed,
        FeeMinimum = terms.Minimum,
        Source = source,
    };

    static Task<int> InsertAsync(LedgerDbContext db, Charge charge, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(
            InsertSql,
            [
                new NpgsqlParameter("transactionId", charge.TransactionId),
                new NpgsqlParameter("currency", charge.Currency.Value),
                Numeric("chargedAmount", charge.ChargedAmount),
                Numeric("feeAmount", charge.FeeAmount),
                Numeric("rateUsed", charge.RateUsed),
                Numeric("feePercent", charge.FeePercent),
                Numeric("feeFixed", charge.FeeFixed),
                Numeric("feeMinimum", charge.FeeMinimum),
                new NpgsqlParameter("source", (object)(int)charge.Source),
            ],
            cancellationToken);

    static NpgsqlParameter Numeric(string name, decimal? value) =>
        new(name, NpgsqlDbType.Numeric) { Value = (object?)value ?? DBNull.Value };
}
