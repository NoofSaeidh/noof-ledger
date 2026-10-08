using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Diagnostics.Integrity;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Noof.Ledger.Persistence.Diagnostics.Integrity;

// I-4: something has waited on the operator for over a day. The bullets are tried in spec order and the first that
// matches is the finding, so a record is listed once (P-2, P-5).
internal sealed class NotAppliedCheck(LedgerDbContext db, IReceiptStore receiptStore, TimeProvider timeProvider)
    : IIntegrityCheck
{
    public static readonly TimeSpan WaitingAfter = TimeSpan.FromHours(24);

    const int MaxErrorLength = 500;
    static readonly char[] LineBreaks = ['\r', '\n'];

    // Narrowed by status and by the set of records with a failed job before the clock and the joins run:
    // categorization_jobs has no index on transaction_id, so the correlated subqueries must see few rows.
    const string CandidatesSql = $"""
        SELECT t.id AS "TransactionId", t.wallet_id AS "WalletId", t.status AS "Status",
               t.failure_reason AS "FailureReason", t.occurred_on AS "OccurredOn",
               activity.last_activity AS "LastActivity",
               {IntegritySql.AwaitingCandidate} AS "AwaitingCandidate",
               EXISTS (
                   SELECT 1 FROM transaction_revisions cancelled
                   WHERE cancelled.transaction_id = t.id AND cancelled.kind = 3) AS "EverCancelled",
               t.id IN (SELECT failed.transaction_id FROM categorization_jobs failed WHERE failed.status = 3) AS "HasFailedJob",
               unapplied.id AS "UnappliedJobId", unapplied.kind AS "UnappliedJobKind",
               unapplied.last_error AS "UnappliedJobError",
               tr.from_amount AS "TransferAmount", tr.from_currency AS "TransferCurrency",
               rc.total AS "ReceiptTotal", rc.currency AS "ReceiptCurrency"
        FROM transactions t
        CROSS JOIN LATERAL (SELECT {IntegritySql.LastActivity} AS last_activity) activity
        LEFT JOIN LATERAL (
            SELECT failed.id, failed.kind, failed.last_error
            FROM categorization_jobs failed
            WHERE failed.transaction_id = t.id
              AND failed.status = 3
              AND NOT EXISTS (
                  SELECT 1 FROM categorization_jobs later
                  WHERE later.transaction_id = t.id AND later.status = 2 AND later.created_at > failed.created_at)
            ORDER BY failed.created_at DESC, failed.id DESC
            LIMIT 1) unapplied ON TRUE
        LEFT JOIN transfers tr ON tr.transaction_id = t.id
        LEFT JOIN receipts rc ON rc.transaction_id = t.id
        WHERE (@transactionId IS NULL OR t.id = @transactionId)
          AND t.status <> 3
          AND (t.status IN (0, 2)
               OR t.id IN (SELECT failed.transaction_id FROM categorization_jobs failed WHERE failed.status = 3))
          AND NOT EXISTS (
              SELECT 1 FROM categorization_jobs open_job
              WHERE open_job.transaction_id = t.id AND open_job.status IN (0, 1))
          AND activity.last_activity < @idleSince
        ORDER BY t.created_at, t.id
        """;

    const string PrincipalSumsSql = """
        SELECT li.transaction_id AS "TransactionId", li.currency AS "Currency", SUM(li.amount) AS "Amount"
        FROM line_items li
        WHERE li.transaction_id = ANY(@transactionIds) AND li.role = 0
        GROUP BY li.transaction_id, li.currency
        ORDER BY li.transaction_id, li.currency
        """;

    public IntegrityCheck Check => IntegrityCheck.NotApplied;

    public IntegrityGroup Group => IntegrityGroup.WaitingOnYou;

    public async Task<IReadOnlyList<IntegrityFinding>> FindAsync(IntegrityScope scope, CancellationToken cancellationToken)
    {
        var candidates = await db.Database.SqlQueryRaw<NotAppliedRow>(
                CandidatesSql,
                IntegrityRows.ScopeParameter(scope),
                new NpgsqlParameter("idleSince", timeProvider.GetUtcNow() - WaitingAfter))
            .ToListAsync(cancellationToken);

        List<(NotAppliedRow Row, Waiting Waiting)> found = [];
        foreach (var row in candidates)
        {
            if (await WaitingForAsync(row, cancellationToken) is { } waiting)
                found.Add((row, waiting));
        }

        if (found.Count == 0)
            return [];

        var principals = await PrincipalSumsAsync([.. found.Select(f => f.Row.TransactionId)], cancellationToken);

        return
        [
            .. found.Select(f => new IntegrityFinding(
                Check, Group, f.Row.TransactionId, f.Row.WalletId, f.Waiting.JobId,
                [
                    .. f.Waiting.Facts,
                    new SinceFact("Idle for", f.Row.LastActivity),
                    new DateFact("Date", f.Row.OccurredOn),
                    .. AmountFacts(f.Row, principals[f.Row.TransactionId]),
                ])),
        ];
    }

    async Task<Waiting?> WaitingForAsync(NotAppliedRow row, CancellationToken cancellationToken)
    {
        var status = (TransactionStatus)row.Status;
        var awaiting = status == TransactionStatus.Captured && await IsAwaitingAsync(row, cancellationToken);

        return (status, awaiting, row) switch
        {
            (TransactionStatus.Failed, _, _) => new Waiting(
            [
                new TextFact("Waiting for", "A reply to the echo"),
                new TextFact("Status", nameof(TransactionStatus.Failed)),
                new TextFact("Reason", ((RecordFailureReason)row.FailureReason).ToString()),
            ]),
            (_, true, _) => new Waiting(
            [
                new TextFact("Waiting for", "Record anyway"),
                new TextFact("Status", nameof(TransactionStatus.Captured)),
            ]),
            (_, _, { UnappliedJobId: { } jobId, UnappliedJobKind: { } kind }) => new Waiting(
            [
                new TextFact("Waiting for", "A correction that never applied"),
                new TextFact("Job", ((JobKind)kind).ToString()),
                new TextFact("Last error", FirstLine(row.UnappliedJobError)),
            ], jobId),
            (TransactionStatus.Captured, _, { EverCancelled: true, HasFailedJob: false }) => new Waiting(
            [
                new TextFact("Waiting for", "Cancel, or a correction reply"),
                new TextFact("Status", nameof(TransactionStatus.Captured)),
            ]),
            _ => null,
        };
    }

    // One call per candidate, and the loop stops between calls: the whole run lives inside one health check's 5 s.
    async Task<bool> IsAwaitingAsync(NotAppliedRow row, CancellationToken cancellationToken)
    {
        if (!row.AwaitingCandidate)
            return false;

        cancellationToken.ThrowIfCancellationRequested();
        return await receiptStore.IsAwaitingConfirmationAsync(row.TransactionId, cancellationToken);
    }

    // The first line only (spec §1): a last error can carry a whole stack trace.
    static string FirstLine(string? lastError)
    {
        var line = (lastError ?? "").Trim().Split(LineBreaks, 2)[0].TrimEnd();
        return line switch
        {
            "" => "(none)",
            { Length: > MaxErrorLength } => string.Concat(line.AsSpan(0, MaxErrorLength - 1), "…"),
            _ => line,
        };
    }

    // P-6: what the record holds — its transfer's source leg, else its receipt's total, else its principal lines per
    // currency, else nothing.
    static IReadOnlyList<IntegrityFact> AmountFacts(NotAppliedRow row, IEnumerable<PrincipalSumRow> principals) => row switch
    {
        { TransferAmount: { } amount, TransferCurrency: { } currency } => [Amount(amount, currency)],
        { ReceiptTotal: { } total, ReceiptCurrency: { } currency } when total > 0m => [Amount(total, currency)],
        _ => [.. principals.Select(sum => Amount(sum.Amount, sum.Currency))],
    };

    static MoneyFact Amount(decimal amount, string currency) => new("Amount", amount, new CurrencyCode(currency));

    async Task<ILookup<Guid, PrincipalSumRow>> PrincipalSumsAsync(Guid[] transactionIds, CancellationToken cancellationToken)
    {
        var sums = await db.Database.SqlQueryRaw<PrincipalSumRow>(
                PrincipalSumsSql,
                new NpgsqlParameter("transactionIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = transactionIds })
            .ToListAsync(cancellationToken);

        return sums.ToLookup(sum => sum.TransactionId);
    }

    sealed record Waiting(IReadOnlyList<IntegrityFact> Facts, Guid? JobId = null);
}

internal sealed class NotAppliedRow
{
    public Guid TransactionId { get; init; }
    public Guid? WalletId { get; init; }
    public int Status { get; init; }
    public int FailureReason { get; init; }
    public DateOnly OccurredOn { get; init; }
    public DateTimeOffset LastActivity { get; init; }
    public bool AwaitingCandidate { get; init; }
    public bool EverCancelled { get; init; }
    public bool HasFailedJob { get; init; }
    public Guid? UnappliedJobId { get; init; }
    public int? UnappliedJobKind { get; init; }
    public string? UnappliedJobError { get; init; }
    public decimal? TransferAmount { get; init; }
    public string? TransferCurrency { get; init; }
    public decimal? ReceiptTotal { get; init; }
    public string? ReceiptCurrency { get; init; }
}

internal sealed class PrincipalSumRow
{
    public Guid TransactionId { get; init; }
    public string Currency { get; init; } = string.Empty;
    public decimal Amount { get; init; }
}
