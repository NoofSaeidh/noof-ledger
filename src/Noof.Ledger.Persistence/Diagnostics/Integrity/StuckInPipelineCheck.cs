using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Diagnostics.Integrity;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Domain;
using Npgsql;

namespace Noof.Ledger.Persistence.Diagnostics.Integrity;

// I-3: a Captured record nothing is working on. A record ever cancelled is I-4's (P-5): Restore never queues work, so
// the app's own cancels followed by the operator's Restore are not a fault of the write path.
internal sealed class StuckInPipelineCheck(LedgerDbContext db, IReceiptStore receiptStore, TimeProvider timeProvider)
    : IIntegrityCheck
{
    public static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(10);

    const string CandidatesSql = $"""
        SELECT t.id AS "TransactionId", t.wallet_id AS "WalletId", activity.last_activity AS "LastActivity",
               {IntegritySql.AwaitingCandidate} AS "AwaitingCandidate"
        FROM transactions t
        CROSS JOIN LATERAL (SELECT {IntegritySql.LastActivity} AS last_activity) activity
        WHERE t.status = 0
          AND (@transactionId IS NULL OR t.id = @transactionId)
          AND NOT EXISTS (
              SELECT 1 FROM categorization_jobs open_job
              WHERE open_job.transaction_id = t.id AND open_job.status IN (0, 1, 3))
          AND NOT EXISTS (
              SELECT 1 FROM transaction_revisions cancelled
              WHERE cancelled.transaction_id = t.id AND cancelled.kind = 3)
          AND activity.last_activity < @idleSince
        ORDER BY t.created_at, t.id
        """;

    public IntegrityCheck Check => IntegrityCheck.StuckInPipeline;

    public IntegrityGroup Group => IntegrityGroup.Bug;

    public async Task<IReadOnlyList<IntegrityFinding>> FindAsync(IntegrityScope scope, CancellationToken cancellationToken)
    {
        var candidates = await db.Database.SqlQueryRaw<StuckInPipelineRow>(
                CandidatesSql,
                IntegrityRows.ScopeParameter(scope),
                new NpgsqlParameter("idleSince", timeProvider.GetUtcNow() - StuckAfter))
            .ToListAsync(cancellationToken);

        List<StuckInPipelineRow> stuck = [];
        foreach (var row in candidates)
        {
            if (!await IsAwaitingAsync(row, cancellationToken))
                stuck.Add(row);
        }

        if (stuck.Count == 0)
            return [];

        var jobs = await JobsByRecordAsync([.. stuck.Select(row => row.TransactionId)], cancellationToken);

        return
        [
            .. stuck.Select(row => new IntegrityFinding(
                Check, Group, row.TransactionId, row.WalletId, null,
                [
                    new TextFact("Status", nameof(TransactionStatus.Captured)),
                    new SinceFact("Idle for", row.LastActivity),
                    new TextFact("Jobs", jobs.GetValueOrDefault(row.TransactionId, "none")),
                ])),
        ];
    }

    // One call per candidate, and the loop stops between calls: the whole run lives inside one health check's 5 s.
    async Task<bool> IsAwaitingAsync(StuckInPipelineRow row, CancellationToken cancellationToken)
    {
        if (!row.AwaitingCandidate)
            return false;

        cancellationToken.ThrowIfCancellationRequested();
        return await receiptStore.IsAwaitingConfirmationAsync(row.TransactionId, cancellationToken);
    }

    async Task<Dictionary<Guid, string>> JobsByRecordAsync(List<Guid> transactionIds, CancellationToken cancellationToken)
    {
        var jobs = await db.CategorizationJobs.AsNoTracking()
            .Where(job => transactionIds.Contains(job.TransactionId))
            .OrderBy(job => job.CreatedAt).ThenBy(job => job.Id)
            .Select(job => new { job.TransactionId, job.Kind, job.Status })
            .ToListAsync(cancellationToken);

        return jobs
            .GroupBy(job => job.TransactionId)
            .ToDictionary(group => group.Key, group => string.Join(", ", group.Select(job => $"{job.Kind} {job.Status}")));
    }
}

internal sealed class StuckInPipelineRow
{
    public Guid TransactionId { get; init; }
    public Guid? WalletId { get; init; }
    public DateTimeOffset LastActivity { get; init; }
    public bool AwaitingCandidate { get; init; }
}
