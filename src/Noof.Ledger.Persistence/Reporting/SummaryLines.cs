using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Reporting.Summary;
using Noof.Ledger.Domain;
using Npgsql;

namespace Noof.Ledger.Persistence.Reporting;

// The lines a month counts and the wallet each belongs to: one reading for Home's This month and for the summary, so
// the two never disagree about what was spent (8b spec §2 "Rows"). Spending is an expense's Principal lines plus every
// Fee line, a transfer's included; income is an income's Principal lines; roles are filtered explicitly, never trusted
// from the kind. A transfer's fee belongs to its fee_leg's wallet, as LedgerPostings posts it. A line on a record with
// no wallet keeps a null wallet here; only the summary drops it.
internal static class SummaryLines
{
    public const string CountedCte = """
        WITH counted AS (
            SELECT li.id AS line_id,
                   t.occurred_on,
                   COALESCE(CASE tr.fee_leg WHEN @fromLeg THEN tr.from_wallet_id WHEN @toLeg THEN tr.to_wallet_id END,
                            t.wallet_id) AS wallet_id
            FROM line_items li
            JOIN transactions t ON t.id = li.transaction_id
            LEFT JOIN transfers tr ON tr.transaction_id = t.id
            WHERE t.status <> @cancelled
              AND ((li.role = @principal AND t.kind IN (@expense, @income))
                OR (li.role = @fee AND t.kind IN (@expense, @transfer)))
        )
        """;

    // A refund is a Principal line of an income whose receipt is a fiscal Refund (SS-19); a charge prices an expense's
    // Principal lines of its currency (A-1); the merchant is the record's (P-7) - a transfer's venue, else its first
    // Principal line's - on every line of it.
    const string LinesSql = CountedCte + "\n" + """
        SELECT t.id AS "TransactionId",
               cl.occurred_on AS "OccurredOn",
               w.id AS "WalletId",
               w.currency AS "WalletCurrency",
               CASE
                   WHEN li.role = @fee OR t.kind = @expense THEN @spent
                   WHEN EXISTS (SELECT 1 FROM receipts r
                                WHERE r.transaction_id = t.id AND r.receipt_kind = @refundReceipt) THEN @refund
                   ELSE @received
               END AS "Kind",
               li.role AS "Role",
               li.ordinal AS "Ordinal",
               COALESCE(c.name_en, @uncategorised) AS "CategoryName",
               CASE WHEN t.kind = @transfer THEN venue.display_name ELSE first_principal.display_name END AS "MerchantName",
               li.description AS "Description",
               li.amount AS "Amount",
               li.currency AS "Currency",
               ch.charged_amount AS "ChargedAmount"
        FROM counted cl
        JOIN line_items li ON li.id = cl.line_id
        JOIN transactions t ON t.id = li.transaction_id
        LEFT JOIN wallets w ON w.id = cl.wallet_id
        LEFT JOIN categories c ON c.id = li.category_id
        LEFT JOIN transfers tr ON tr.transaction_id = t.id
        LEFT JOIN merchants venue ON venue.id = tr.venue_merchant_id
        LEFT JOIN LATERAL (
            SELECT m.display_name
            FROM line_items p
            LEFT JOIN merchants m ON m.id = p.merchant_id
            WHERE p.transaction_id = t.id AND p.role = @principal
            ORDER BY p.ordinal, p.id
            LIMIT 1
        ) first_principal ON TRUE
        LEFT JOIN charges ch ON ch.transaction_id = t.id AND ch.currency = li.currency
            AND t.kind = @expense AND li.role = @principal
        WHERE cl.occurred_on >= @from
          AND cl.occurred_on < @toExclusive
        ORDER BY cl.occurred_on, t.id, li.ordinal, li.id
        """;

    public static Task<List<SummaryLineSqlRow>> ReadAsync(
        LedgerDbContext db, DateOnly from, DateOnly toExclusive, CancellationToken cancellationToken) =>
        db.Database.SqlQueryRaw<SummaryLineSqlRow>(
                LinesSql,
                [
                    .. CountedParameters(),
                    .. Range(from, toExclusive),
                    Integer("spent", (int)SummaryLineKind.Spent),
                    Integer("received", (int)SummaryLineKind.Received),
                    Integer("refund", (int)SummaryLineKind.Refund),
                    Integer("refundReceipt", (int)ReceiptKind.Refund),
                    new NpgsqlParameter("uncategorised", EfSpendingReadModel.UncategorisedLabel),
                ])
            .ToListAsync(cancellationToken);

    public static NpgsqlParameter[] CountedParameters() =>
    [
        Integer("cancelled", (int)TransactionStatus.Cancelled),
        Integer("expense", (int)TransactionKind.Expense),
        Integer("income", (int)TransactionKind.Income),
        Integer("transfer", (int)TransactionKind.Transfer),
        Integer("principal", (int)EntryRole.Principal),
        Integer("fee", (int)EntryRole.Fee),
        Integer("fromLeg", (int)TransferLeg.From),
        Integer("toLeg", (int)TransferLeg.To),
    ];

    public static NpgsqlParameter[] Range(DateOnly from, DateOnly toExclusive) =>
    [
        new("from", from),
        new("toExclusive", toExclusive),
    ];

    // A constant 0 converts implicitly to any enum, so new NpgsqlParameter("x", (int)TransactionKind.Expense) - or
    // EntryRole.Principal - matches both the NpgsqlDbType and the DbType overloads and does not compile (CS0121, an
    // ambiguous call). An int parameter is not a constant, so this always reaches (string, object).
    public static NpgsqlParameter Integer(string name, int value) => new(name, value);
}

internal sealed class SummaryLineSqlRow
{
    public Guid TransactionId { get; init; }
    public DateOnly OccurredOn { get; init; }
    public Guid? WalletId { get; init; }
    public string? WalletCurrency { get; init; }
    public int Kind { get; init; }
    public int Role { get; init; }
    public int Ordinal { get; init; }
    public string CategoryName { get; init; } = string.Empty;
    public string? MerchantName { get; init; }
    public string Description { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public string Currency { get; init; } = string.Empty;
    public decimal? ChargedAmount { get; init; }
}
