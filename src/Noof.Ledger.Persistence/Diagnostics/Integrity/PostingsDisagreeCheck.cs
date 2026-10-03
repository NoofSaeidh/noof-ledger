using Noof.Ledger.Application.Diagnostics.Integrity;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Diagnostics.Integrity;

// I-1: a record's entries differ from what its own facts add up to. The oracle LedgerWritePathTests holds the write
// path to, so it is derived independently of LedgerPostings, which writes the entries.
internal sealed class PostingsDisagreeCheck(LedgerDbContext db) : IIntegrityCheck
{
    // Expected entries, derived from the stored facts: an expense's or income's lines per currency and role on the
    // record's wallet, less any Principal line a charge prices; each charge as its charged amount in the wallet's
    // currency; a transfer's source principal (its stored amount less a fee taken there), its destination principal
    // (its stored amount plus a fee taken there), and its fee on the wallet of the fee's leg (T-12). Summed per key,
    // since a charge and the wallet-currency lines share one. A transfer's fee is one sum per record, so a second
    // Fee line (an I-2 fault) cannot multiply the legs' rows.
    const string DisagreeingSql = """
        WITH transfer_fees AS (
            SELECT tr.transaction_id, coalesce(SUM(li.amount), 0) AS amount
            FROM transfers tr
            LEFT JOIN line_items li ON li.transaction_id = tr.transaction_id AND li.role = 1
            GROUP BY tr.transaction_id
        ),
        expected AS (
            SELECT transaction_id, wallet_id, currency, role, SUM(amount) AS amount
            FROM (
                SELECT t.id AS transaction_id, t.wallet_id, li.currency, li.role,
                       CASE t.kind WHEN 1 THEN 1 ELSE -1 END * li.amount AS amount
                FROM transactions t
                JOIN line_items li ON li.transaction_id = t.id
                WHERE t.kind IN (0, 1)
                  AND NOT (li.role = 0 AND EXISTS (
                      SELECT 1 FROM charges c WHERE c.transaction_id = t.id AND c.currency = li.currency))
                UNION ALL
                SELECT t.id, t.wallet_id, w.currency, 0, -c.charged_amount
                FROM charges c
                JOIN transactions t ON t.id = c.transaction_id
                JOIN wallets w ON w.id = t.wallet_id
                UNION ALL
                SELECT tr.transaction_id, tr.from_wallet_id, tr.from_currency, 0,
                       -(tr.from_amount - CASE WHEN tr.fee_leg = 0 THEN fee.amount ELSE 0 END)
                FROM transfers tr
                JOIN transfer_fees fee ON fee.transaction_id = tr.transaction_id
                UNION ALL
                SELECT tr.transaction_id, tr.to_wallet_id, tr.to_currency, 0,
                       tr.to_amount + CASE WHEN tr.fee_leg = 1 THEN fee.amount ELSE 0 END
                FROM transfers tr
                JOIN transfer_fees fee ON fee.transaction_id = tr.transaction_id
                UNION ALL
                SELECT tr.transaction_id, CASE tr.fee_leg WHEN 0 THEN tr.from_wallet_id ELSE tr.to_wallet_id END,
                       li.currency, 1, -li.amount
                FROM transfers tr
                JOIN line_items li ON li.transaction_id = tr.transaction_id AND li.role = 1
            ) parts
            GROUP BY transaction_id, wallet_id, currency, role
        ),
        posted AS (
            SELECT transaction_id, wallet_id, currency, role, SUM(amount) AS amount
            FROM entries
            GROUP BY transaction_id, wallet_id, currency, role
        ),
        disagreeing AS (
            SELECT coalesce(e.transaction_id, p.transaction_id) AS transaction_id,
                   coalesce(e.wallet_id, p.wallet_id) AS wallet_id,
                   coalesce(e.currency, p.currency) AS currency,
                   coalesce(e.role, p.role) AS role,
                   e.amount AS expected_amount,
                   p.amount AS posted_amount
            FROM expected e
            FULL JOIN posted p ON p.transaction_id = e.transaction_id AND p.wallet_id = e.wallet_id
                AND p.currency = e.currency AND p.role = e.role
            WHERE e.amount IS DISTINCT FROM p.amount
        )
        SELECT d.transaction_id AS "TransactionId", t.created_at AS "CreatedAt", t.wallet_id AS "RecordWalletId",
               coalesce(w.name, '(none)') AS "WalletName", d.currency AS "Currency", d.role AS "Role",
               d.expected_amount AS "Expected", d.posted_amount AS "Posted"
        FROM disagreeing d
        JOIN transactions t ON t.id = d.transaction_id
        LEFT JOIN wallets w ON w.id = d.wallet_id
        WHERE (@transactionId IS NULL OR d.transaction_id = @transactionId)
        """;

    // An entry on neither the record's wallet nor its transfer's destination.
    const string MisplacedSql = """
        SELECT t.id AS "TransactionId", t.created_at AS "CreatedAt", t.wallet_id AS "RecordWalletId",
               w.name AS "WalletName", e.currency AS "Currency", e.amount AS "Amount"
        FROM entries e
        JOIN transactions t ON t.id = e.transaction_id
        JOIN wallets w ON w.id = e.wallet_id
        LEFT JOIN transfers tr ON tr.transaction_id = t.id
        WHERE e.wallet_id IS DISTINCT FROM t.wallet_id
          AND e.wallet_id IS DISTINCT FROM tr.to_wallet_id
          AND (@transactionId IS NULL OR t.id = @transactionId)
        """;

    public IntegrityCheck Check => IntegrityCheck.PostingsDisagree;

    public IntegrityGroup Group => IntegrityGroup.Bug;

    public async Task<IReadOnlyList<IntegrityFinding>> FindAsync(IntegrityScope scope, CancellationToken cancellationToken)
    {
        var disagreeing = await IntegrityRows.ReadAsync<DisagreeingRow>(db, DisagreeingSql, scope, cancellationToken);
        var misplaced = await IntegrityRows.ReadAsync<MisplacedEntryRow>(db, MisplacedSql, scope, cancellationToken);

        IEnumerable<RecordKey> records =
        [
            .. disagreeing.Select(row => new RecordKey(row.TransactionId, row.CreatedAt, row.RecordWalletId)),
            .. misplaced.Select(row => new RecordKey(row.TransactionId, row.CreatedAt, row.RecordWalletId)),
        ];

        return
        [
            .. records
                .Distinct()
                .OrderBy(record => record.CreatedAt)
                .ThenBy(record => record.TransactionId)
                .Select(record => new IntegrityFinding(
                    Check, Group, record.TransactionId, record.WalletId, null,
                    [
                        .. disagreeing
                            .Where(row => row.TransactionId == record.TransactionId)
                            .OrderBy(row => row.WalletName, StringComparer.Ordinal)
                            .ThenBy(row => row.Currency, StringComparer.Ordinal)
                            .ThenBy(row => row.Role)
                            .SelectMany(DisagreementFacts),
                        .. misplaced
                            .Where(row => row.TransactionId == record.TransactionId)
                            .OrderBy(row => row.WalletName, StringComparer.Ordinal)
                            .ThenBy(row => row.Currency, StringComparer.Ordinal)
                            .ThenBy(row => row.Amount)
                            .SelectMany(MisplacedFacts),
                    ])),
        ];
    }

    static IEnumerable<IntegrityFact> DisagreementFacts(DisagreeingRow row)
    {
        var currency = new CurrencyCode(row.Currency);
        return
        [
            new TextFact("Wallet", row.WalletName),
            new TextFact("Role", ((EntryRole)row.Role).ToString()),
            new MoneyFact("Expected", row.Expected ?? 0m, currency),
            new MoneyFact("Posted", row.Posted ?? 0m, currency),
        ];
    }

    static IEnumerable<IntegrityFact> MisplacedFacts(MisplacedEntryRow row) =>
    [
        new TextFact("Entry on another wallet", row.WalletName),
        new MoneyFact("Entry", row.Amount, new CurrencyCode(row.Currency)),
    ];

    readonly record struct RecordKey(Guid TransactionId, DateTimeOffset CreatedAt, Guid? WalletId);
}

internal sealed class DisagreeingRow
{
    public Guid TransactionId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public Guid? RecordWalletId { get; init; }
    public string WalletName { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;
    public int Role { get; init; }
    public decimal? Expected { get; init; }
    public decimal? Posted { get; init; }
}

internal sealed class MisplacedEntryRow
{
    public Guid TransactionId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public Guid? RecordWalletId { get; init; }
    public string WalletName { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;
    public decimal Amount { get; init; }
}
