using Microsoft.EntityFrameworkCore;
using Npgsql;
using Noof.Ledger.Application.Jobs;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Jobs;

public sealed class EfJobQueue(LedgerDbContext db, TimeProvider timeProvider, int maxAttempts) : IJobQueue
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
                    SELECT id FROM categorization_jobs
                    WHERE status = 0 AND run_after <= @now
                    ORDER BY run_after
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED
                )
                RETURNING id, transaction_id, status, attempt_count, run_after, claimed_at, claimed_by, last_error, created_at, updated_at
                """,
                new NpgsqlParameter("now", now),
                new NpgsqlParameter("workerId", workerId),
                new NpgsqlParameter("leaseExpiry", leaseExpiry))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return claimed.SingleOrDefault();
    }

    public Task SucceedAsync(Guid jobId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(
            "UPDATE categorization_jobs SET status = 2, updated_at = @now WHERE id = @jobId",
            [new NpgsqlParameter("now", timeProvider.GetUtcNow()), new NpgsqlParameter("jobId", jobId)],
            cancellationToken);

    public Task RetryAsync(Guid jobId, DateTimeOffset runAfter, string error, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(
            """
            UPDATE categorization_jobs
            SET status = CASE WHEN attempt_count >= @maxAttempts THEN 3 ELSE 0 END,
                run_after = CASE WHEN attempt_count >= @maxAttempts THEN run_after ELSE @runAfter END,
                claimed_at = NULL,
                claimed_by = NULL,
                last_error = @error,
                updated_at = @now
            WHERE id = @jobId
            """,
            [
                new NpgsqlParameter("maxAttempts", maxAttempts),
                new NpgsqlParameter("runAfter", runAfter),
                new NpgsqlParameter("error", error),
                new NpgsqlParameter("now", timeProvider.GetUtcNow()),
                new NpgsqlParameter("jobId", jobId),
            ],
            cancellationToken);

    public Task FailAsync(Guid jobId, string error, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(
            "UPDATE categorization_jobs SET status = 3, last_error = @error, updated_at = @now WHERE id = @jobId",
            [new NpgsqlParameter("error", error), new NpgsqlParameter("now", timeProvider.GetUtcNow()), new NpgsqlParameter("jobId", jobId)],
            cancellationToken);

    public Task<int> ReleaseExpiredLeasesAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(
            "UPDATE categorization_jobs SET status = 0, claimed_at = NULL, claimed_by = NULL, updated_at = @now WHERE status = 1 AND run_after <= @now",
            [new NpgsqlParameter("now", now)],
            cancellationToken);
}
