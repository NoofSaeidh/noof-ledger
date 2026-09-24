using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Noof.Ledger.Persistence.Migrations;

/// <inheritdoc />
public partial class AddWalletBalancesView : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE VIEW public.wallet_balances AS
            WITH completed AS (
                -- 1 is TransactionStatus.Completed: nothing captured, failed or cancelled moves a balance.
                SELECT id, occurred_on, occurred_at, created_at
                FROM public.transactions
                WHERE status = 1
            ),
            latest_checkpoints AS (
                SELECT DISTINCT ON (bc.wallet_id, bc.currency)
                       bc.wallet_id, bc.currency, bc.stated_amount, c.occurred_on, c.occurred_at
                FROM public.balance_checks bc
                JOIN completed c ON c.id = bc.transaction_id
                -- Two statements at the same (occurred_on, occurred_at): the one recorded later stands, then the
                -- higher id, so the answer never depends on the heap.
                ORDER BY bc.wallet_id, bc.currency, c.occurred_on DESC, c.occurred_at DESC, c.created_at DESC, c.id DESC
            ),
            counted_entries AS (
                SELECT e.wallet_id, e.currency, e.amount, c.occurred_on, c.occurred_at
                FROM public.entries e
                JOIN completed c ON c.id = e.transaction_id
            ),
            pairs AS (
                SELECT wallet_id, currency FROM latest_checkpoints
                UNION
                SELECT wallet_id, currency FROM counted_entries
            )
            SELECT p.wallet_id,
                   CAST(p.currency AS varchar(3)) AS currency,
                   CAST(COALESCE(k.stated_amount, 0) + COALESCE(SUM(e.amount), 0) AS numeric(19,4)) AS balance,
                   k.occurred_on AS checked_on
            FROM pairs p
            LEFT JOIN latest_checkpoints k ON k.wallet_id = p.wallet_id AND k.currency = p.currency
            -- Strictly after: an entry at exactly the checkpoint's own moment is already inside the stated amount.
            LEFT JOIN counted_entries e ON e.wallet_id = p.wallet_id AND e.currency = p.currency
                AND (k.wallet_id IS NULL OR (e.occurred_on, e.occurred_at) > (k.occurred_on, k.occurred_at))
            GROUP BY p.wallet_id, p.currency, k.stated_amount, k.occurred_on;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP VIEW IF EXISTS public.wallet_balances;");
    }
}
