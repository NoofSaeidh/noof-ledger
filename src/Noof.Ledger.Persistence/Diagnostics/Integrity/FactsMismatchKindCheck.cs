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

    const string ChargeOnAnotherKindSql = """
        SELECT t.id AS "TransactionId", t.created_at AS "CreatedAt", t.wallet_id AS "WalletId",
               t.kind AS "Kind", NULL::int AS "Status", NULL::int AS "FailureReason", NULL::int AS "Count",
               NULL::int AS "Leg", c.currency::text AS "Currency", NULL::text AS "WalletCurrency",
               NULL::text AS "RecordWallet", NULL::text AS "OtherWallet"
        FROM transactions t
        JOIN charges c ON c.transaction_id = t.id
        WHERE t.kind <> 0
          AND (@transactionId IS NULL OR t.id = @transactionId)
        """;

    const string ChargeInWalletCurrencySql = """
        SELECT t.id AS "TransactionId", t.created_at AS "CreatedAt", t.wallet_id AS "WalletId",
               NULL::int AS "Kind", NULL::int AS "Status", NULL::int AS "FailureReason", NULL::int AS "Count",
               NULL::int AS "Leg", c.currency::text AS "Currency", NULL::text AS "WalletCurrency",
               NULL::text AS "RecordWallet", NULL::text AS "OtherWallet"
        FROM transactions t
        JOIN charges c ON c.transaction_id = t.id
        JOIN wallets w ON w.id = t.wallet_id
        WHERE c.currency = w.currency
          AND (@transactionId IS NULL OR t.id = @transactionId)
        """;

    const string ExpenseFeeLineInAnotherCurrencySql = """
        SELECT t.id AS "TransactionId", t.created_at AS "CreatedAt", t.wallet_id AS "WalletId",
               NULL::int AS "Kind", NULL::int AS "Status", NULL::int AS "FailureReason", NULL::int AS "Count",
               NULL::int AS "Leg", li.currency::text AS "Currency", w.currency::text AS "WalletCurrency",
               NULL::text AS "RecordWallet", NULL::text AS "OtherWallet"
        FROM transactions t
        JOIN line_items li ON li.transaction_id = t.id
        JOIN wallets w ON w.id = t.wallet_id
        WHERE t.kind = 0
          AND li.role = 1
          AND li.currency <> w.currency
          AND (@transactionId IS NULL OR t.id = @transactionId)
        """;

    const string FeeLegWithoutOneFeeLineSql = """
        SELECT t.id AS "TransactionId", t.created_at AS "CreatedAt", t.wallet_id AS "WalletId",
               NULL::int AS "Kind", NULL::int AS "Status", NULL::int AS "FailureReason", fee.lines AS "Count",
               tr.fee_leg AS "Leg", NULL::text AS "Currency", NULL::text AS "WalletCurrency",
               NULL::text AS "RecordWallet", NULL::text AS "OtherWallet"
        FROM transactions t
        JOIN transfers tr ON tr.transaction_id = t.id
        CROSS JOIN LATERAL (
            SELECT count(*)::int AS lines FROM line_items li WHERE li.transaction_id = t.id AND li.role = 1
        ) fee
        WHERE t.kind = 3
          AND tr.fee_leg IS NOT NULL
          AND fee.lines <> 1
          AND (@transactionId IS NULL OR t.id = @transactionId)
        """;

    const string FeeLineWithoutFeeLegSql = """
        SELECT t.id AS "TransactionId", t.created_at AS "CreatedAt", t.wallet_id AS "WalletId",
               NULL::int AS "Kind", NULL::int AS "Status", NULL::int AS "FailureReason", fee.lines AS "Count",
               NULL::int AS "Leg", NULL::text AS "Currency", NULL::text AS "WalletCurrency",
               NULL::text AS "RecordWallet", NULL::text AS "OtherWallet"
        FROM transactions t
        JOIN transfers tr ON tr.transaction_id = t.id
        CROSS JOIN LATERAL (
            SELECT count(*)::int AS lines FROM line_items li WHERE li.transaction_id = t.id AND li.role = 1
        ) fee
        WHERE t.kind = 3
          AND tr.fee_leg IS NULL
          AND fee.lines > 0
          AND (@transactionId IS NULL OR t.id = @transactionId)
        """;

    const string LegInAnotherCurrencySql = """
        SELECT t.id AS "TransactionId", t.created_at AS "CreatedAt", t.wallet_id AS "WalletId",
               NULL::int AS "Kind", NULL::int AS "Status", NULL::int AS "FailureReason", NULL::int AS "Count",
               leg.number AS "Leg", leg.currency::text AS "Currency", w.currency::text AS "WalletCurrency",
               NULL::text AS "RecordWallet", NULL::text AS "OtherWallet"
        FROM transactions t
        JOIN transfers tr ON tr.transaction_id = t.id
        CROSS JOIN LATERAL (
            VALUES (0, tr.from_wallet_id, tr.from_currency), (1, tr.to_wallet_id, tr.to_currency)
        ) AS leg(number, wallet_id, currency)
        JOIN wallets w ON w.id = leg.wallet_id
        WHERE t.kind = 3
          AND leg.currency <> w.currency
          AND (@transactionId IS NULL OR t.id = @transactionId)
        """;

    const string CheckpointOnAnotherWalletSql = """
        SELECT t.id AS "TransactionId", t.created_at AS "CreatedAt", t.wallet_id AS "WalletId",
               NULL::int AS "Kind", NULL::int AS "Status", NULL::int AS "FailureReason", NULL::int AS "Count",
               NULL::int AS "Leg", NULL::text AS "Currency", NULL::text AS "WalletCurrency",
               coalesce(rw.name, '(none)')::text AS "RecordWallet", cw.name::text AS "OtherWallet"
        FROM transactions t
        JOIN balance_checks bc ON bc.transaction_id = t.id
        JOIN wallets cw ON cw.id = bc.wallet_id
        LEFT JOIN wallets rw ON rw.id = t.wallet_id
        WHERE t.kind = 2
          AND bc.wallet_id IS DISTINCT FROM t.wallet_id
          AND (@transactionId IS NULL OR t.id = @transactionId)
        """;

    const string SourceIsNotRecordWalletSql = """
        SELECT t.id AS "TransactionId", t.created_at AS "CreatedAt", t.wallet_id AS "WalletId",
               NULL::int AS "Kind", NULL::int AS "Status", NULL::int AS "FailureReason", NULL::int AS "Count",
               NULL::int AS "Leg", NULL::text AS "Currency", NULL::text AS "WalletCurrency",
               coalesce(rw.name, '(none)')::text AS "RecordWallet", sw.name::text AS "OtherWallet"
        FROM transactions t
        JOIN transfers tr ON tr.transaction_id = t.id
        JOIN wallets sw ON sw.id = tr.from_wallet_id
        LEFT JOIN wallets rw ON rw.id = t.wallet_id
        WHERE t.kind = 3
          AND t.wallet_id IS DISTINCT FROM tr.from_wallet_id
          AND (@transactionId IS NULL OR t.id = @transactionId)
        """;

    // IR-11: a Failed record keeps its reason, and so does a Cancelled one, for its echo and its Restore.
    const string FailureReasonOnUnfailedSql = """
        SELECT t.id AS "TransactionId", t.created_at AS "CreatedAt", t.wallet_id AS "WalletId",
               NULL::int AS "Kind", t.status AS "Status", t.failure_reason AS "FailureReason", NULL::int AS "Count",
               NULL::int AS "Leg", NULL::text AS "Currency", NULL::text AS "WalletCurrency",
               NULL::text AS "RecordWallet", NULL::text AS "OtherWallet"
        FROM transactions t
        WHERE t.status IN (0, 1)
          AND t.failure_reason <> 0
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
        new("Charge on a record that is not an expense", ChargeOnAnotherKindSql,
            rows => [KindFact(rows[0]), .. ByCurrency(rows).Select(row => TextOf("Charge currency", row.Currency))]),
        new("Charge in the wallet's own currency", ChargeInWalletCurrencySql,
            rows => [.. ByCurrency(rows).Select(row => TextOf("Charge currency", row.Currency))]),
        new("Expense fee line in another currency than the wallet's", ExpenseFeeLineInAnotherCurrencySql,
            rows =>
            [
                .. ByCurrency(rows).Select(row => TextOf("Fee line currency", row.Currency)),
                TextOf("Wallet currency", rows[0].WalletCurrency),
            ]),
        new("Fee leg without exactly one fee line", FeeLegWithoutOneFeeLineSql,
            rows => [new TextFact("Fee leg", LegName(rows[0].Leg)), new CountFact("Fee lines", rows[0].Count.GetValueOrDefault())]),
        new("Fee line on a transfer without a fee leg", FeeLineWithoutFeeLegSql,
            rows => [new CountFact("Fee lines", rows[0].Count.GetValueOrDefault())]),
        new("Transfer leg in another currency than its wallet's", LegInAnotherCurrencySql,
            rows => [.. rows.OrderBy(row => row.Leg).SelectMany(LegFacts)]),
        new("Checkpoint on another wallet than the record's", CheckpointOnAnotherWalletSql,
            rows => [TextOf("Record wallet", rows[0].RecordWallet), TextOf("Checkpoint wallet", rows[0].OtherWallet)]),
        new("Transfer source is not the record's wallet", SourceIsNotRecordWalletSql,
            rows => [TextOf("Record wallet", rows[0].RecordWallet), TextOf("Source wallet", rows[0].OtherWallet)]),
        new("Failure reason on a record that is not failed", FailureReasonOnUnfailedSql,
            rows =>
            [
                new TextFact("Status", ((TransactionStatus)rows[0].Status.GetValueOrDefault()).ToString()),
                new TextFact("Failure reason", ((RecordFailureReason)rows[0].FailureReason.GetValueOrDefault()).ToString()),
            ]),
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

    static TextFact TextOf(string name, string? value) => new(name, value ?? string.Empty);

    static string LegName(int? leg) => ((TransferLeg)leg.GetValueOrDefault()).ToString();

    static IEnumerable<FactsMismatchRow> ByCurrency(IEnumerable<FactsMismatchRow> rows) =>
        rows.OrderBy(row => row.Currency, StringComparer.Ordinal);

    static IEnumerable<IntegrityFact> LegFacts(FactsMismatchRow row) =>
    [
        new TextFact("Leg", LegName(row.Leg)),
        TextOf("Leg currency", row.Currency),
        TextOf("Wallet currency", row.WalletCurrency),
    ];

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
