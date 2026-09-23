using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Jobs;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Jobs;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfJobQueueTests(PostgresFixture fixture)
{
    static int nextTelegramMessageId;

    static Wallet NewWallet() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Cash",
        Currency = CurrencyCode.Eur,
        IsDefault = false,
    };

    static Transaction NewTransaction(Guid walletId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        WalletId = walletId,
        RawText = "coffee 3.50",
        Status = TransactionStatus.Captured,
        TimeZoneId = "Europe/Belgrade",
        OccurredAt = now,
        OccurredOn = DateOnly.FromDateTime(now.UtcDateTime),
        TelegramChatId = 1,
        TelegramMessageId = Interlocked.Increment(ref nextTelegramMessageId),
        CreatedAt = now,
    };

    // categorization_jobs.transaction_id is a real foreign key
    // (FK_categorization_jobs_transactions_transaction_id) - a job cannot reference a
    // transaction that doesn't exist, so every test seeds a real wallet + transaction first
    // rather than pointing TransactionId at a bare Guid.NewGuid().
    static async Task<Guid> SeedTransactionAsync(LedgerDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var wallet = NewWallet();
        var transaction = NewTransaction(wallet.Id, now);
        db.Wallets.Add(wallet);
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(cancellationToken);
        return transaction.Id;
    }

    static CategorizationJob NewJob(
        Guid transactionId, DateTimeOffset runAfter, JobStatus status = JobStatus.Pending, int attemptCount = 0,
        JobKind kind = JobKind.Categorize, string? instruction = null, int? sourceMessageId = null,
        DateTimeOffset? createdAt = null) => new()
    {
        Id = Guid.NewGuid(),
        TransactionId = transactionId,
        Status = status,
        AttemptCount = attemptCount,
        RunAfter = runAfter,
        Kind = kind,
        Instruction = instruction,
        SourceMessageId = sourceMessageId,
        CreatedAt = createdAt ?? runAfter,
        UpdatedAt = createdAt ?? runAfter,
    };

    [Fact]
    public async Task Claims_a_pending_job_that_is_due()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var queue = new EfJobQueue(db, time, maxAttempts: 8);
        var transactionId = await SeedTransactionAsync(db, time.GetUtcNow(), TestContext.Current.CancellationToken);
        var job = NewJob(transactionId, time.GetUtcNow().AddMinutes(-1));
        db.CategorizationJobs.Add(job);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var claimed = await queue.ClaimAsync("worker-a", TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);

        claimed.Should().NotBeNull();
        claimed!.Id.Should().Be(job.Id);
        claimed.Status.Should().Be(JobStatus.Claimed);
        claimed.ClaimedBy.Should().Be("worker-a");
        claimed.AttemptCount.Should().Be(1, "claiming is what counts as an attempt");
        claimed.RunAfter.Should().BeCloseTo(time.GetUtcNow() + TimeSpan.FromMinutes(15), TimeSpan.FromMilliseconds(1),
            "run_after now doubles as the lease deadline");
    }

    [Fact]
    public async Task Returns_null_when_nothing_is_due()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var queue = new EfJobQueue(db, time, maxAttempts: 8);
        var transactionId = await SeedTransactionAsync(db, time.GetUtcNow(), TestContext.Current.CancellationToken);
        db.CategorizationJobs.Add(NewJob(transactionId, time.GetUtcNow().AddMinutes(5)));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var claimed = await queue.ClaimAsync("worker-a", TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);

        claimed.Should().BeNull();
    }

    [Fact]
    public async Task Concurrent_claims_against_one_pending_job_return_it_to_exactly_one_caller()
    {
        await using var dbA = await fixture.CreateContextAsync();
        await dbA.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var transactionId = await SeedTransactionAsync(dbA, time.GetUtcNow(), TestContext.Current.CancellationToken);
        dbA.CategorizationJobs.Add(NewJob(transactionId, time.GetUtcNow().AddMinutes(-1)));
        await dbA.SaveChangesAsync(TestContext.Current.CancellationToken);

        var optionsB = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(dbA.Database.GetConnectionString())
            .Options;
        await using var dbB = new LedgerDbContext(optionsB);

        var queueA = new EfJobQueue(dbA, time, maxAttempts: 8);
        var queueB = new EfJobQueue(dbB, time, maxAttempts: 8);

        await using var txA = await dbA.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);

        var claimedByA = await queueA.ClaimAsync("worker-a", TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);
        claimedByA.Should().NotBeNull("worker-a claimed first and still holds the row lock inside its open transaction");

        var claimBTask = queueB.ClaimAsync("worker-b", TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(claimBTask, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        finished.Should().BeSameAs(claimBTask,
            "SKIP LOCKED must make worker-b's claim return immediately; plain FOR UPDATE would block until worker-a commits, and this test would hang instead of failing cleanly");
        (await claimBTask).Should().BeNull("the only pending job is locked by worker-a's still-open transaction");

        await txA.CommitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Succeeding_a_job_marks_it_succeeded()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var queue = new EfJobQueue(db, time, maxAttempts: 8);
        var transactionId = await SeedTransactionAsync(db, time.GetUtcNow(), TestContext.Current.CancellationToken);
        var job = NewJob(transactionId, time.GetUtcNow().AddMinutes(-1));
        db.CategorizationJobs.Add(job);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await queue.ClaimAsync("worker-a", TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);

        var outcome = await queue.SucceedAsync(job.Id, "worker-a", TestContext.Current.CancellationToken);

        outcome.Should().Be(JobCompletionOutcome.Applied);
        var reloaded = await db.CategorizationJobs.AsNoTracking()
            .SingleAsync(j => j.Id == job.Id, TestContext.Current.CancellationToken);
        reloaded.Status.Should().Be(JobStatus.Succeeded);
    }

    [Fact]
    public async Task Succeeding_a_job_this_worker_no_longer_owns_is_reported_as_not_owned_and_leaves_the_row_alone()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var queue = new EfJobQueue(db, time, maxAttempts: 8);
        var transactionId = await SeedTransactionAsync(db, time.GetUtcNow(), TestContext.Current.CancellationToken);
        var job = NewJob(transactionId, time.GetUtcNow().AddMinutes(-1));
        db.CategorizationJobs.Add(job);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await queue.ClaimAsync("worker-a", TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(2));
        await queue.ReleaseExpiredLeasesAsync(time.GetUtcNow(), TestContext.Current.CancellationToken);
        await queue.ClaimAsync("worker-b", TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);

        var outcome = await queue.SucceedAsync(job.Id, "worker-a", TestContext.Current.CancellationToken);

        outcome.Should().Be(JobCompletionOutcome.NotOwned,
            "worker-a's lease already expired and was reclaimed by worker-b before this call arrived");
        var reloaded = await db.CategorizationJobs.AsNoTracking()
            .SingleAsync(j => j.Id == job.Id, TestContext.Current.CancellationToken);
        reloaded.Status.Should().Be(JobStatus.Claimed, "worker-a's stale success must not touch worker-b's claim");
        reloaded.ClaimedBy.Should().Be("worker-b");
    }

    [Fact]
    public async Task A_stale_workers_late_retry_after_its_lease_was_reclaimed_does_not_resurrect_the_job()
    {
        // Reproduces the reviewer's interleaving: worker-a claims with a short lease, the clock
        // advances past it, ReleaseExpiredLeasesAsync hands it to worker-b, worker-b succeeds, and
        // only then does worker-a's late RetryAsync arrive. Before this fix that UPDATE had no
        // ownership guard at all and would flip the row straight back to Pending with a pushed-out
        // run_after even though worker-b had already finished it - a third worker would then claim
        // and redo work that already succeeded.
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var queue = new EfJobQueue(db, time, maxAttempts: 8);
        var transactionId = await SeedTransactionAsync(db, time.GetUtcNow(), TestContext.Current.CancellationToken);
        var job = NewJob(transactionId, time.GetUtcNow().AddMinutes(-1));
        db.CategorizationJobs.Add(job);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await queue.ClaimAsync("worker-a", TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(2));
        var released = await queue.ReleaseExpiredLeasesAsync(time.GetUtcNow(), TestContext.Current.CancellationToken);
        released.Should().Be(1, "worker-a's one-minute lease is two minutes stale by now");
        await queue.ClaimAsync("worker-b", TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);
        var succeedOutcome = await queue.SucceedAsync(job.Id, "worker-b", TestContext.Current.CancellationToken);
        succeedOutcome.Should().Be(JobCompletionOutcome.Applied);

        var lateRetryOutcome = await queue.RetryAsync(
            job.Id, "worker-a", time.GetUtcNow().AddMinutes(1), "worker-a's late failure", TestContext.Current.CancellationToken);

        lateRetryOutcome.Should().Be(JobCompletionOutcome.NotOwned);
        var reloaded = await db.CategorizationJobs.AsNoTracking()
            .SingleAsync(j => j.Id == job.Id, TestContext.Current.CancellationToken);
        reloaded.Status.Should().Be(JobStatus.Succeeded, "worker-b's completion must survive worker-a's late retry");
        reloaded.ClaimedBy.Should().Be("worker-b");
        reloaded.LastError.Should().BeNull();
    }

    [Fact]
    public async Task A_stale_workers_late_fail_after_its_lease_was_reclaimed_does_not_override_the_new_owner()
    {
        // The FailAsync variant the reviewer called out: a released job's new claim must not be
        // knocked straight to Failed by the original worker's late failure report.
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var queue = new EfJobQueue(db, time, maxAttempts: 8);
        var transactionId = await SeedTransactionAsync(db, time.GetUtcNow(), TestContext.Current.CancellationToken);
        var job = NewJob(transactionId, time.GetUtcNow().AddMinutes(-1));
        db.CategorizationJobs.Add(job);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await queue.ClaimAsync("worker-a", TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(2));
        await queue.ReleaseExpiredLeasesAsync(time.GetUtcNow(), TestContext.Current.CancellationToken);
        await queue.ClaimAsync("worker-b", TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);

        var outcome = await queue.FailAsync(job.Id, "worker-a", "worker-a's late failure", TestContext.Current.CancellationToken);

        outcome.Should().Be(JobCompletionOutcome.NotOwned);
        var reloaded = await db.CategorizationJobs.AsNoTracking()
            .SingleAsync(j => j.Id == job.Id, TestContext.Current.CancellationToken);
        reloaded.Status.Should().Be(JobStatus.Claimed, "worker-b still owns this job; worker-a's late fail must not touch it");
        reloaded.ClaimedBy.Should().Be("worker-b");
        reloaded.LastError.Should().BeNull();
    }

    [Fact]
    public async Task Retrying_below_the_attempt_cap_returns_the_job_to_pending()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var queue = new EfJobQueue(db, time, maxAttempts: 2);
        var transactionId = await SeedTransactionAsync(db, time.GetUtcNow(), TestContext.Current.CancellationToken);
        var job = NewJob(transactionId, time.GetUtcNow().AddMinutes(-1));
        db.CategorizationJobs.Add(job);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await queue.ClaimAsync("worker-a", TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);
        var nextRunAfter = time.GetUtcNow().AddMinutes(1);

        var outcome = await queue.RetryAsync(job.Id, "worker-a", nextRunAfter, "boom", TestContext.Current.CancellationToken);

        outcome.Should().Be(JobCompletionOutcome.Applied);
        var reloaded = await db.CategorizationJobs.AsNoTracking()
            .SingleAsync(j => j.Id == job.Id, TestContext.Current.CancellationToken);
        reloaded.Status.Should().Be(JobStatus.Pending, "attempt 1 of a 2-attempt cap still has a retry left");
        reloaded.RunAfter.Should().BeCloseTo(nextRunAfter, TimeSpan.FromMilliseconds(1));
        reloaded.LastError.Should().Be("boom");
        reloaded.ClaimedBy.Should().BeNull();
    }

    [Fact]
    public async Task Retrying_at_the_attempt_cap_fails_the_job_terminally_instead()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var queue = new EfJobQueue(db, time, maxAttempts: 2);
        var transactionId = await SeedTransactionAsync(db, time.GetUtcNow(), TestContext.Current.CancellationToken);
        var job = NewJob(transactionId, time.GetUtcNow().AddMinutes(-1));
        db.CategorizationJobs.Add(job);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await queue.ClaimAsync("worker-a", TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);
        await queue.RetryAsync(job.Id, "worker-a", time.GetUtcNow().AddMinutes(1), "first failure", TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(2));
        await queue.ClaimAsync("worker-a", TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);

        var outcome = await queue.RetryAsync(job.Id, "worker-a", time.GetUtcNow().AddMinutes(1), "second failure", TestContext.Current.CancellationToken);

        outcome.Should().Be(JobCompletionOutcome.Applied);
        var reloaded = await db.CategorizationJobs.AsNoTracking()
            .SingleAsync(j => j.Id == job.Id, TestContext.Current.CancellationToken);
        reloaded.Status.Should().Be(JobStatus.Failed, "attempt 2 of a 2-attempt cap has no retries left");
        reloaded.LastError.Should().Be("second failure");
        reloaded.AttemptCount.Should().Be(2);
    }

    [Fact]
    public async Task Failing_a_job_this_worker_owns_marks_it_failed()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var queue = new EfJobQueue(db, time, maxAttempts: 8);
        var transactionId = await SeedTransactionAsync(db, time.GetUtcNow(), TestContext.Current.CancellationToken);
        var job = NewJob(transactionId, time.GetUtcNow().AddMinutes(-1));
        db.CategorizationJobs.Add(job);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await queue.ClaimAsync("worker-a", TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);

        var outcome = await queue.FailAsync(job.Id, "worker-a", "not a transaction", TestContext.Current.CancellationToken);

        outcome.Should().Be(JobCompletionOutcome.Applied);
        var reloaded = await db.CategorizationJobs.AsNoTracking()
            .SingleAsync(j => j.Id == job.Id, TestContext.Current.CancellationToken);
        reloaded.Status.Should().Be(JobStatus.Failed);
        reloaded.LastError.Should().Be("not a transaction");
    }

    [Fact]
    public async Task Releases_only_the_leases_that_have_actually_expired()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var queue = new EfJobQueue(db, time, maxAttempts: 8);

        var expiredTransactionId = await SeedTransactionAsync(db, time.GetUtcNow(), TestContext.Current.CancellationToken);
        var expired = NewJob(expiredTransactionId, time.GetUtcNow().AddMinutes(-20), status: JobStatus.Claimed, attemptCount: 1);
        expired.ClaimedAt = time.GetUtcNow().AddMinutes(-35);
        expired.ClaimedBy = "worker-a";

        var stillLeasedTransactionId = await SeedTransactionAsync(db, time.GetUtcNow(), TestContext.Current.CancellationToken);
        var stillLeased = NewJob(stillLeasedTransactionId, time.GetUtcNow().AddMinutes(10), status: JobStatus.Claimed, attemptCount: 1);
        stillLeased.ClaimedAt = time.GetUtcNow().AddMinutes(-5);
        stillLeased.ClaimedBy = "worker-b";

        db.CategorizationJobs.AddRange(expired, stillLeased);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var released = await queue.ReleaseExpiredLeasesAsync(time.GetUtcNow(), TestContext.Current.CancellationToken);

        released.Should().Be(1);
        var reloadedExpired = await db.CategorizationJobs.AsNoTracking()
            .SingleAsync(j => j.Id == expired.Id, TestContext.Current.CancellationToken);
        reloadedExpired.Status.Should().Be(JobStatus.Pending);
        reloadedExpired.ClaimedAt.Should().BeNull();
        reloadedExpired.ClaimedBy.Should().BeNull();

        var reloadedStillLeased = await db.CategorizationJobs.AsNoTracking()
            .SingleAsync(j => j.Id == stillLeased.Id, TestContext.Current.CancellationToken);
        reloadedStillLeased.Status.Should().Be(JobStatus.Claimed, "its lease has not expired yet");
    }

    [Fact]
    public async Task A_later_job_for_the_same_transaction_waits_for_the_earlier_one()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var now = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var queue = new EfJobQueue(db, time, maxAttempts: 8);
        var corrected = await SeedTransactionAsync(db, now, TestContext.Current.CancellationToken);
        var other = await SeedTransactionAsync(db, now, TestContext.Current.CancellationToken);

        // The first reading is backing off after a transient failure; the correction behind it is due.
        // Claiming the correction first would apply it, then let the older reading overwrite it.
        db.CategorizationJobs.AddRange(
            NewJob(corrected, runAfter: now.AddMinutes(5), createdAt: now.AddMinutes(-10)),
            NewJob(corrected, runAfter: now.AddMinutes(-1), kind: JobKind.Correct, instruction: "нет, 1500",
                sourceMessageId: 7, createdAt: now.AddMinutes(-5)));
        var otherJob = NewJob(other, runAfter: now.AddMinutes(-1), createdAt: now.AddMinutes(-4));
        db.CategorizationJobs.Add(otherJob);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var claimed = await queue.ClaimAsync("worker-a", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        claimed!.Id.Should().Be(otherJob.Id);
        (await queue.ClaimAsync("worker-a", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken))
            .Should().BeNull("the correction must wait until the first reading is no longer pending");
    }

    [Fact]
    public async Task A_claimed_correction_carries_its_kind_instruction_and_source_message()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var now = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var queue = new EfJobQueue(db, new FakeTimeProvider(now), maxAttempts: 8);
        var transactionId = await SeedTransactionAsync(db, now, TestContext.Current.CancellationToken);
        db.CategorizationJobs.Add(NewJob(transactionId, now.AddMinutes(-1), kind: JobKind.Correct, instruction: "нет, 1500", sourceMessageId: 7));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var claimed = await queue.ClaimAsync("worker-a", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        claimed!.Kind.Should().Be(JobKind.Correct);
        claimed.Instruction.Should().Be("нет, 1500");
        claimed.SourceMessageId.Should().Be(7);
    }

    [Fact]
    public async Task A_correction_without_an_instruction_is_refused_by_the_database()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var now = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var transactionId = await SeedTransactionAsync(db, now, TestContext.Current.CancellationToken);
        db.CategorizationJobs.Add(NewJob(transactionId, now, kind: JobKind.Correct));

        var act = () => db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task RetryAsync_rejects_a_non_UTC_runAfter_with_a_clear_message_instead_of_an_Npgsql_failure()
    {
        // Before this guard, a non-UTC runAfter reached Npgsql unvalidated and failed three layers
        // down with "Cannot write DateTimeOffset with Offset=02:00:00 to PostgreSQL type 'timestamp
        // with time zone'" - confirmed by reproducing it against real Postgres. That message names
        // neither the parameter nor the actual contract. This guard fires before any SQL is sent.
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var queue = new EfJobQueue(db, time, maxAttempts: 8);
        var nonUtc = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(2));

        var act = () => queue.RetryAsync(Guid.NewGuid(), "worker-a", nonUtc, "boom", TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<ArgumentException>();
        assertion.WithMessage("*UTC*");
        assertion.And.ParamName.Should().Be("runAfter");
    }

    [Fact]
    public async Task ReleaseExpiredLeasesAsync_rejects_a_non_UTC_now_with_a_clear_message_instead_of_an_Npgsql_failure()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var queue = new EfJobQueue(db, time, maxAttempts: 8);
        var nonUtc = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(2));

        var act = () => queue.ReleaseExpiredLeasesAsync(nonUtc, TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<ArgumentException>();
        assertion.WithMessage("*UTC*");
        assertion.And.ParamName.Should().Be("now");
    }
}
