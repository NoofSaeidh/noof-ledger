using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;
using Npgsql;

namespace Noof.Ledger.Persistence.Balances;

// What a record does to its wallet's balance, replaced whole every time the record is written: an expense's or
// an income's signed entries, or a balance statement's checkpoint (M5, M6). Called inside the caller's database
// transaction, after the record and its lines are saved and while the caller holds the record's row lock, so the
// entries can never disagree with the lines they are summed from. Nothing here stores a balance.
internal static class LedgerPostings
{
    // The rule the AddMoneyModel backfill applied to every expense that existed before entries did: one entry per
    // currency, the sum of the lines, negative for an expense and positive for an income. A record with lines and
    // no wallet cannot be posted; entries.wallet_id NOT NULL refuses it and the whole write rolls back.
    const string InsertEntriesSql = """
        INSERT INTO entries (id, transaction_id, wallet_id, amount, currency, role)
        SELECT gen_random_uuid(), t.id, t.wallet_id, @sign * SUM(li.amount), li.currency, @role
        FROM transactions t
        JOIN line_items li ON li.transaction_id = t.id
        WHERE t.id = @transactionId
        GROUP BY t.id, t.wallet_id, li.currency
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

    public static async Task RewriteAsync(
        LedgerDbContext db, Transaction transaction, Money? stated, CancellationToken cancellationToken)
    {
        await DeleteAsync(db, "DELETE FROM entries WHERE transaction_id = @transactionId", transaction.Id, cancellationToken);

        if (transaction.Kind != TransactionKind.BalanceCheck)
        {
            await DeleteAsync(db, "DELETE FROM balance_checks WHERE transaction_id = @transactionId", transaction.Id, cancellationToken);
            await db.Database.ExecuteSqlRawAsync(
                InsertEntriesSql,
                [
                    new NpgsqlParameter("transactionId", transaction.Id),
                    new NpgsqlParameter("sign", transaction.Kind == TransactionKind.Income ? 1 : -1),
                    new NpgsqlParameter("role", (object)(int)EntryRole.Principal),
                ],
                cancellationToken);
            return;
        }

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

    static Task<int> DeleteAsync(LedgerDbContext db, string sql, Guid transactionId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(sql, [new NpgsqlParameter("transactionId", transactionId)], cancellationToken);
}
