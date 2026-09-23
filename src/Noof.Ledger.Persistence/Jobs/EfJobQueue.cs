using Microsoft.EntityFrameworkCore;
using Npgsql;
using Noof.Ledger.Application.Jobs;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Jobs;

internal sealed class EfJobQueue(LedgerDbContext db, TimeProvider timeProvider, int maxAttempts) : IJobQueue
{
    public async Task<CategorizationJob?> ClaimAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var leaseExpiry = now + lease;

        var claimed = await db.CategorizationJobs
            .FromSqlRaw(
                """
                UPDATE categorization_jobs
                SET status = 1,
                    claimed_at = @now,
                    claimed_by = @workerId,
                    attempt_count = attempt_count + 1,
                    run_after = @leaseExpiry,
                    updated_at = @now
                WHERE id = (
                    SELECT j.id FROM categorization_jobs j
                    WHERE j.status = 0 AND j.run_after <= @now
                      AND NOT EXISTS (
                          SELECT 1 FROM categorization_jobs earlier
                          WHERE earlier.transaction_id = j.transaction_id
                            AND earlier.status IN (0, 1)
                            AND earlier.created_at < j.created_at)
                    ORDER BY j.run_after
                    LIMIT 1
                    FOR UPDATE OF j SKIP LOCKED
                )
                RETURNING id, transaction_id, status, attempt_count, run_after, claimed_at, claimed_by, last_error,
                          created_at, updated_at, kind, instruction, source_message_id
                """,
                new NpgsqlParameter("now", now),
                new NpgsqlParameter("workerId", workerId),
                new NpgsqlParameter("leaseExpiry", leaseExpiry))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        // NOT EXISTS keeps one transaction's jobs in the order they were asked for: a correction must
        // never be applied before the reading it corrects, which would then overwrite it.
        return claimed.SingleOrDefault();
    }

    public async Task<JobCompletionOutcome> SucceedAsync(Guid jobId, string workerId, CancellationToken cancellationToken)
    {
        var rows = await db.Database.ExecuteSqlRawAsync(
            "UPDATE categorization_jobs SET status = 2, updated_at = @now WHERE id = @jobId AND claimed_by = @workerId AND status = 1",
            [
                new NpgsqlParameter("now", timeProvider.GetUtcNow()),
                new NpgsqlParameter("jobId", jobId),
                new NpgsqlParameter("workerId", workerId),
            ],
            cancellationToken);

        return ToOutcome(rows);
    }

    public async Task<JobCompletionOutcome> RetryAsync(Guid jobId, string workerId, DateTimeOffset runAfter, string error, CancellationToken cancellationToken)
    {
        RequireUtc(runAfter, nameof(runAfter));

        var rows = await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE categorization_jobs
            SET status = CASE WHEN attempt_count >= @maxAttempts THEN 3 ELSE 0 END,
                run_after = CASE WHEN attempt_count >= @maxAttempts THEN run_after ELSE @runAfter END,
                claimed_at = NULL,
                claimed_by = NULL,
                last_error = @error,
                updated_at = @now
            WHERE id = @jobId AND claimed_by = @workerId AND status = 1
            """,
            [
                new NpgsqlParameter("maxAttempts", maxAttempts),
                new NpgsqlParameter("runAfter", runAfter),
                new NpgsqlParameter("error", error),
                new NpgsqlParameter("now", timeProvider.GetUtcNow()),
                new NpgsqlParameter("jobId", jobId),
                new NpgsqlParameter("workerId", workerId),
            ],
            cancellationToken);

        return ToOutcome(rows);
    }

    public async Task<JobCompletionOutcome> FailAsync(Guid jobId, string workerId, string error, CancellationToken cancellationToken)
    {
        var rows = await db.Database.ExecuteSqlRawAsync(
            "UPDATE categorization_jobs SET status = 3, last_error = @error, updated_at = @now WHERE id = @jobId AND claimed_by = @workerId AND status = 1",
            [
                new NpgsqlParameter("error", error),
                new NpgsqlParameter("now", timeProvider.GetUtcNow()),
                new NpgsqlParameter("jobId", jobId),
                new NpgsqlParameter("workerId", workerId),
            ],
            cancellationToken);

        return ToOutcome(rows);
    }

    static JobCompletionOutcome ToOutcome(int rowsAffected) =>
        rowsAffected > 0 ? JobCompletionOutcome.Applied : JobCompletionOutcome.NotOwned;

    public Task<int> ReleaseExpiredLeasesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        RequireUtc(now, nameof(now));

        return db.Database.ExecuteSqlRawAsync(
            "UPDATE categorization_jobs SET status = 0, claimed_at = NULL, claimed_by = NULL, updated_at = @now WHERE status = 1 AND run_after <= @now",
            [new NpgsqlParameter("now", now)],
            cancellationToken);
    }

    // Postgres timestamptz has no offset of its own - Npgsql rejects a non-UTC DateTimeOffset only
    // once it tries to write it, three layers below this method, with a message that names neither
    // the parameter nor the actual rule ("Cannot write DateTimeOffset with Offset=... to PostgreSQL
    // type 'timestamp with time zone'"). Guarding here instead makes the UTC-only contract fail
    // loudly at the call site, with the parameter name and the reason, before any SQL is sent.
    static void RequireUtc(DateTimeOffset value, string paramName)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new ArgumentException($"must be UTC (Offset == TimeSpan.Zero), but was {value.Offset}.", paramName);
    }
}
