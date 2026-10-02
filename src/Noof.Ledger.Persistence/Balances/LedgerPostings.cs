using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Noof.Ledger.Persistence.Balances;

// What a record does to its wallets' balances, replaced whole every time the record is written: an expense's or an
// income's signed entries, a transfer's two legs and its fee, or a balance statement's checkpoint (M5, M6, Phase 7
// spec §1 "Posting"). Called inside the caller's database transaction, after the record and its lines are saved and
// while the caller holds the record's row lock, so the entries can never disagree with the facts they are derived
// from. Nothing here stores a balance.
internal static class LedgerPostings
{
    // The rule the AddMoneyModel backfill applied to every expense that existed before entries did: one entry per
    // currency, the sum of the lines, negative for an expense and positive for an income - now per role too, so a fee
    // line is a Fee entry. A record with lines and no wallet cannot be posted; entries.wallet_id NOT NULL refuses it
    // and the whole write rolls back.
    const string InsertLineEntriesSql = """
        INSERT INTO entries (id, transaction_id, wallet_id, amount, currency, role)
        SELECT gen_random_uuid(), t.id, t.wallet_id, @sign * SUM(li.amount), li.currency, li.role
        FROM transactions t
        JOIN line_items li ON li.transaction_id = t.id
        WHERE t.id = @transactionId AND li.role = @role
        GROUP BY t.id, t.wallet_id, li.currency, li.role
        """;

    // An expense's Principal entries (spec §1 "Posting → Expense"): its Principal lines in the wallet's currency, and
    // in any foreign currency no charge prices (M10), as their own sum; each charge as its charged amount in the
    // wallet's currency, in the same entry as the wallet-currency lines. One entry per currency.
    const string InsertExpensePrincipalsSql = """
        INSERT INTO entries (id, transaction_id, wallet_id, amount, currency, role)
        SELECT gen_random_uuid(), t.id, t.wallet_id, -SUM(posted.amount), posted.currency, @principal
        FROM transactions t
        CROSS JOIN (
            SELECT li.amount, li.currency
            FROM line_items li
            WHERE li.transaction_id = @transactionId AND li.role = @principal
              AND NOT EXISTS (
                  SELECT 1 FROM charges c WHERE c.transaction_id = li.transaction_id AND c.currency = li.currency)
            UNION ALL
            SELECT c.charged_amount, w.currency
            FROM charges c
            JOIN transactions ct ON ct.id = c.transaction_id
            JOIN wallets w ON w.id = ct.wallet_id
            WHERE c.transaction_id = @transactionId
        ) posted
        WHERE t.id = @transactionId
        GROUP BY t.id, t.wallet_id, posted.currency
        """;

    const string InsertEntrySql = """
        INSERT INTO entries (id, transaction_id, wallet_id, amount, currency, role)
        VALUES (gen_random_uuid(), @transactionId, @walletId, @amount, @currency, @role)
        """;

    // SQL rather than a tracked entity, like the deletes: the row is replaced whole, and a BalanceCheck still
    // tracked from an earlier write in the same context would collide with a new instance under the same key.
    const string UpsertCheckpointSql = """
        INSERT INTO balance_checks (transaction_id, wallet_id, stated_amount, currency, computed_before)
        VALUES (@transactionId, @walletId, @statedAmount, @currency, @computedBefore)
        ON CONFLICT (transaction_id) DO UPDATE
        SET wallet_id = EXCLUDED.wallet_id,
            stated_amount = EXCLUDED.stated_amount,
            currency = EXCLUDED.currency,
            computed_before = EXCLUDED.computed_before
        """;

    // The same reasoning as the checkpoint: one row per transfer, replaced whole on every write.
    const string UpsertTransferSql = """
        INSERT INTO transfers (transaction_id, from_wallet_id, from_amount, from_currency, to_wallet_id, to_amount,
                               to_currency, fee_leg, stated_rate, stated_rate_base, venue_merchant_id)
        VALUES (@transactionId, @fromWalletId, @fromAmount, @fromCurrency, @toWalletId, @toAmount,
                @toCurrency, @feeLeg, @statedRate, @statedRateBase, @venueMerchantId)
        ON CONFLICT (transaction_id) DO UPDATE
        SET from_wallet_id = EXCLUDED.from_wallet_id,
            from_amount = EXCLUDED.from_amount,
            from_currency = EXCLUDED.from_currency,
            to_wallet_id = EXCLUDED.to_wallet_id,
            to_amount = EXCLUDED.to_amount,
            to_currency = EXCLUDED.to_currency,
            fee_leg = EXCLUDED.fee_leg,
            stated_rate = EXCLUDED.stated_rate,
            stated_rate_base = EXCLUDED.stated_rate_base,
            venue_merchant_id = EXCLUDED.venue_merchant_id
        """;

    public static async Task RewriteAsync(
        LedgerDbContext db, Transaction transaction, Money? stated, TransferFacts? transfer, CancellationToken cancellationToken)
    {
        await DeleteAsync(db, "DELETE FROM entries WHERE transaction_id = @transactionId", transaction.Id, cancellationToken);

        // Leaving a kind deletes its facts: a record that is no longer a statement keeps no checkpoint, and one that is
        // no longer a transfer keeps no legs.
        if (transaction.Kind != TransactionKind.BalanceCheck)
            await DeleteAsync(db, "DELETE FROM balance_checks WHERE transaction_id = @transactionId", transaction.Id, cancellationToken);
        if (transaction.Kind != TransactionKind.Transfer)
            await DeleteAsync(db, "DELETE FROM transfers WHERE transaction_id = @transactionId", transaction.Id, cancellationToken);

        // One arm per kind and none by default: a kind added later is refused here until it is given a posting of
        // its own, instead of quietly posting as an expense.
        await (transaction.Kind switch
        {
            TransactionKind.Expense => PostExpenseAsync(db, transaction.Id, cancellationToken),
            TransactionKind.Income => PostLinesAsync(db, transaction.Id, EntryRole.Principal, sign: 1, cancellationToken),
            TransactionKind.BalanceCheck => PostCheckpointAsync(db, transaction, stated, cancellationToken),
            TransactionKind.Transfer => PostTransferAsync(
                db,
                transaction.Id,
                transfer ?? throw new InvalidOperationException(
                    $"Transfer {transaction.Id} needs its legs, and EfCategorizationStore always passes them."),
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(transaction), transaction.Kind, "No posting for this kind of record."),
        });
    }

    static async Task PostExpenseAsync(LedgerDbContext db, Guid transactionId, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
            InsertExpensePrincipalsSql,
            [
                new NpgsqlParameter("transactionId", transactionId),
                new NpgsqlParameter("principal", (object)(int)EntryRole.Principal),
            ],
            cancellationToken);
        await PostLinesAsync(db, transactionId, EntryRole.Fee, sign: -1, cancellationToken);
    }

    static async Task PostLinesAsync(
        LedgerDbContext db, Guid transactionId, EntryRole role, int sign, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlRawAsync(
            InsertLineEntriesSql,
            [
                new NpgsqlParameter("transactionId", transactionId),
                new NpgsqlParameter("sign", sign),
                new NpgsqlParameter("role", (int)role),
            ],
            cancellationToken);

    static async Task PostCheckpointAsync(
        LedgerDbContext db, Transaction transaction, Money? stated, CancellationToken cancellationToken)
    {
        // Unreachable while ProposalMapper gives every statement both. If it ever fires, CategorizationWorker treats an
        // unmodelled exception as transient and retries it up to MaxAttempts before failing the job - it is not a
        // terminal mapping failure, and the retries cannot succeed.
        if (stated is not { } statement || transaction.WalletId is not { } walletId)
            throw new InvalidOperationException(
                $"Balance statement {transaction.Id} needs a wallet and a stated amount, and ProposalMapper always gives both.");

        var computedBefore = await BalanceSql.AsOfAsync(
            db, walletId, statement.Currency, transaction.OccurredOn, transaction.OccurredAt, transaction.Id, cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            UpsertCheckpointSql,
            [
                new NpgsqlParameter("transactionId", transaction.Id),
                new NpgsqlParameter("walletId", walletId),
                new NpgsqlParameter("statedAmount", statement.Amount),
                new NpgsqlParameter("currency", statement.Currency.Value),
                new NpgsqlParameter("computedBefore", computedBefore),
            ],
            cancellationToken);
    }

    // T-12: a leg's stored amount is all that moved in its wallet, fee included. So the fee's leg posts its principal
    // without the fee plus a Fee entry of its own, and each wallet's entries sum to exactly its stored amount.
    static async Task PostTransferAsync(
        LedgerDbContext db, Guid transactionId, TransferFacts transfer, CancellationToken cancellationToken)
    {
        await UpsertTransferAsync(db, transactionId, transfer, cancellationToken);

        var sourcePrincipal = transfer is { FeeLeg: TransferLeg.From, Fee: { } sourceFee } ? transfer.From - sourceFee : transfer.From;
        var destinationPrincipal = transfer is { FeeLeg: TransferLeg.To, Fee: { } destinationFee } ? transfer.To + destinationFee : transfer.To;

        await InsertEntryAsync(db, transactionId, transfer.FromWalletId, -sourcePrincipal, EntryRole.Principal, cancellationToken);
        await InsertEntryAsync(db, transactionId, transfer.ToWalletId, destinationPrincipal, EntryRole.Principal, cancellationToken);

        if (transfer is { Fee: { } fee, FeeLeg: { } leg })
            await InsertEntryAsync(
                db, transactionId, leg == TransferLeg.From ? transfer.FromWalletId : transfer.ToWalletId, -fee, EntryRole.Fee,
                cancellationToken);
    }

    static Task<int> UpsertTransferAsync(
        LedgerDbContext db, Guid transactionId, TransferFacts transfer, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(
            UpsertTransferSql,
            [
                new NpgsqlParameter("transactionId", transactionId),
                new NpgsqlParameter("fromWalletId", transfer.FromWalletId),
                new NpgsqlParameter("fromAmount", transfer.From.Amount),
                new NpgsqlParameter("fromCurrency", transfer.From.Currency.Value),
                new NpgsqlParameter("toWalletId", transfer.ToWalletId),
                new NpgsqlParameter("toAmount", transfer.To.Amount),
                new NpgsqlParameter("toCurrency", transfer.To.Currency.Value),
                new NpgsqlParameter("feeLeg", NpgsqlDbType.Integer) { Value = (object?)(int?)transfer.FeeLeg ?? DBNull.Value },
                new NpgsqlParameter("statedRate", NpgsqlDbType.Numeric)
                {
                    Value = (object?)transfer.StatedRate?.QuoteAmount ?? DBNull.Value,
                },
                new NpgsqlParameter("statedRateBase", NpgsqlDbType.Varchar)
                {
                    Value = (object?)transfer.StatedRate?.Base.Value ?? DBNull.Value,
                },
                new NpgsqlParameter("venueMerchantId", NpgsqlDbType.Uuid)
                {
                    Value = (object?)transfer.VenueMerchantId ?? DBNull.Value,
                },
            ],
            cancellationToken);

    static Task<int> InsertEntryAsync(
        LedgerDbContext db, Guid transactionId, Guid walletId, Money amount, EntryRole role, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(
            InsertEntrySql,
            [
                new NpgsqlParameter("transactionId", transactionId),
                new NpgsqlParameter("walletId", walletId),
                new NpgsqlParameter("amount", amount.Amount),
                new NpgsqlParameter("currency", amount.Currency.Value),
                new NpgsqlParameter("role", (int)role),
            ],
            cancellationToken);

    static Task<int> DeleteAsync(LedgerDbContext db, string sql, Guid transactionId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(sql, [new NpgsqlParameter("transactionId", transactionId)], cancellationToken);
}
