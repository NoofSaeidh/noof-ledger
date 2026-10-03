using Noof.Ledger.Application.Diagnostics.Integrity;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Diagnostics.Integrity;

// I-2: a record's stored facts contradict its kind. One query per condition, run in spec §1's order; one finding per
// record and condition. Every query names every column of FactsMismatchRow, NULL where its condition has nothing to
// say, because a raw query must return each property it maps onto.
internal sealed class FactsMismatchKindCheck(LedgerDbContext db) : IIntegrityCheck
{
    const string TransferWithoutLegsSql = """
        SELECT t.id AS "TransactionId", t.created_at AS "CreatedAt", t.wallet_id AS "WalletId",
               NULL::int AS "Kind", NULL::int AS "Status", NULL::int AS "FailureReason", NULL::int AS "Count",
               NULL::int AS "Leg", NULL::text AS "Currency", NULL::text AS "WalletCurrency",
               NULL::text AS "RecordWallet", NULL::text AS "OtherWallet"
        FROM transactions t
        WHERE t.kind = 3
          AND NOT EXISTS (SELECT 1 FROM transfers tr WHERE tr.transaction_id = t.id)
          AND (@transactionId IS NULL OR t.id = @transactionId)
        """;

    const string LegsOnAnotherKindSql = """
        SELECT t.id AS "TransactionId", t.created_at AS "CreatedAt", t.wallet_id AS "WalletId",
               t.kind AS "Kind", NULL::int AS "Status", NULL::int AS "FailureReason", NULL::int AS "Count",
               NULL::int AS "Leg", NULL::text AS "Currency", NULL::text AS "WalletCurrency",
               NULL::text AS "RecordWallet", NULL::text AS "OtherWallet"
        FROM transactions t
        JOIN transfers tr ON tr.transaction_id = t.id
        WHERE t.kind <> 3
          AND (@transactionId IS NULL OR t.id = @transactionId)
        """;

    const string TransferWithPrincipalLineSql = """
        SELECT t.id AS "TransactionId", t.created_at AS "CreatedAt", t.wallet_id AS "WalletId",
               NULL::int AS "Kind", NULL::int AS "Status", NULL::int AS "FailureReason", principal.lines AS "Count",
               NULL::int AS "Leg", NULL::text AS "Currency", NULL::text AS "WalletCurrency",
               NULL::text AS "RecordWallet", NULL::text AS "OtherWallet"
        FROM transactions t
        CROSS JOIN LATERAL (
            SELECT count(*)::int AS lines FROM line_items li WHERE li.transaction_id = t.id AND li.role = 0
        ) principal
        WHERE t.kind = 3
          AND principal.lines > 0
          AND (@transactionId IS NULL OR t.id = @transactionId)
        """;

    const string StatementWithoutCheckpointSql = """
        SELECT t.id AS "TransactionId", t.created_at AS "CreatedAt", t.wallet_id AS "WalletId",
               NULL::int AS "Kind", NULL::int AS "Status", NULL::int AS "FailureReason", NULL::int AS "Count",
               NULL::int AS "Leg", NULL::text AS "Currency", NULL::text AS "WalletCurrency",
               NULL::text AS "RecordWallet", NULL::text AS "OtherWallet"
        FROM transactions t
        WHERE t.kind = 2
          AND NOT EXISTS (SELECT 1 FROM balance_checks bc WHERE bc.transaction_id = t.id)
          AND (@transactionId IS NULL OR t.id = @transactionId)
        """;

    const string CheckpointOnAnotherKindSql = """
        SELECT t.id AS "TransactionId", t.created_at AS "CreatedAt", t.wallet_id AS "WalletId",
               t.kind AS "Kind", NULL::int AS "Status", NULL::int AS "FailureReason", NULL::int AS "Count",
               NULL::int AS "Leg", NULL::text AS "Currency", NULL::text AS "WalletCurrency",
               NULL::text AS "RecordWallet", NULL::text AS "OtherWallet"
        FROM transactions t
        JOIN balance_checks bc ON bc.transaction_id = t.id
        WHERE t.kind <> 2
          AND (@transactionId IS NULL OR t.id = @transactionId)
        """;

    static readonly Condition[] Conditions =
    [
        new("Transfer without its legs", TransferWithoutLegsSql, _ => []),
        new("Transfer legs on a record that is not a transfer", LegsOnAnotherKindSql, rows => [KindFact(rows[0])]),
        new("Transfer with a principal line", TransferWithPrincipalLineSql,
            rows => [new CountFact("Principal lines", rows[0].Count.GetValueOrDefault())]),
        new("Statement without its checkpoint", StatementWithoutCheckpointSql, _ => []),
        new("Checkpoint on a record that is not a statement", CheckpointOnAnotherKindSql, rows => [KindFact(rows[0])]),
    ];

    public IntegrityCheck Check => IntegrityCheck.FactsMismatchKind;

    public IntegrityGroup Group => IntegrityGroup.Bug;

    public async Task<IReadOnlyList<IntegrityFinding>> FindAsync(IntegrityScope scope, CancellationToken cancellationToken)
    {
        List<(DateTimeOffset CreatedAt, Guid TransactionId, IntegrityFinding Finding)> found = [];
        foreach (var condition in Conditions)
        {
            var rows = await IntegrityRows.ReadAsync<FactsMismatchRow>(db, condition.Sql, scope, cancellationToken);
            foreach (var record in rows.GroupBy(row => row.TransactionId))
            {
                var first = record.First();
                found.Add((first.CreatedAt, record.Key, new IntegrityFinding(
                    Check, Group, record.Key, first.WalletId, null,
                    [new TextFact("Condition", condition.Name), .. condition.Facts([.. record])])));
            }
        }

        // A stable sort: a record's findings stay in the conditions' order.
        return [.. found.OrderBy(entry => entry.CreatedAt).ThenBy(entry => entry.TransactionId).Select(entry => entry.Finding)];
    }

    static TextFact KindFact(FactsMismatchRow row) =>
        new("Kind", ((TransactionKind)row.Kind.GetValueOrDefault()).ToString());

    sealed record Condition(
        string Name, string Sql, Func<IReadOnlyList<FactsMismatchRow>, IEnumerable<IntegrityFact>> Facts);
}

internal sealed class FactsMismatchRow
{
    public Guid TransactionId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public Guid? WalletId { get; init; }
    public int? Kind { get; init; }
    public int? Status { get; init; }
    public int? FailureReason { get; init; }
    public int? Count { get; init; }
    public int? Leg { get; init; }
    public string? Currency { get; init; }
    public string? WalletCurrency { get; init; }
    public string? RecordWallet { get; init; }
    public string? OtherWallet { get; init; }
}
