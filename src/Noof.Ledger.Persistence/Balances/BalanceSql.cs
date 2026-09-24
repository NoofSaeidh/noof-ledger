using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Balances;

// The wallet_balances rule cut off at one point of the ledger's order: what a wallet held just before a record, for a
// statement's "balance was". A view takes no parameters, so the rule is written twice, and
// BalanceSqlTests.At_the_end_of_the_ledger_it_agrees_with_the_view holds the two to one answer.
internal static class BalanceSql
{
    public static async Task<decimal> AsOfAsync(
        LedgerDbContext db, Guid walletId, CurrencyCode currency, DateOnly occurredOn, DateTimeOffset occurredAt,
        Guid excludingTransactionId, CancellationToken ct)
    {
        // Npgsql writes only offset-zero values to a timestamptz parameter.
        var cutOff = occurredAt.ToUniversalTime();

        var balance = await db.Database.SqlQuery<decimal>(
            $"""
            WITH completed AS (
                SELECT id, occurred_on, occurred_at, created_at
                FROM public.transactions
                WHERE status = {(int)TransactionStatus.Completed}
                  AND id <> {excludingTransactionId}
                  AND (occurred_on, occurred_at) <= ({occurredOn}, {cutOff})
            ),
            latest_checkpoint AS (
                SELECT bc.stated_amount, c.occurred_on, c.occurred_at
                FROM public.balance_checks bc
                JOIN completed c ON c.id = bc.transaction_id
                WHERE bc.wallet_id = {walletId} AND bc.currency = {currency.Value}
                ORDER BY c.occurred_on DESC, c.occurred_at DESC, c.created_at DESC, c.id DESC
                LIMIT 1
            )
            SELECT CAST(
                COALESCE((SELECT stated_amount FROM latest_checkpoint), 0)
                + COALESCE((
                    SELECT SUM(e.amount)
                    FROM public.entries e
                    JOIN completed c ON c.id = e.transaction_id
                    WHERE e.wallet_id = {walletId} AND e.currency = {currency.Value}
                      AND NOT EXISTS (
                          SELECT 1 FROM latest_checkpoint k
                          WHERE (c.occurred_on, c.occurred_at) <= (k.occurred_on, k.occurred_at))
                ), 0) AS numeric(19,4)) AS "Value"
            """).ToListAsync(ct);

        return balance.Single();
    }
}
