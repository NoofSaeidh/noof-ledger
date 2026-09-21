using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
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

    static CategorizationJob NewJob(Guid transactionId, DateTimeOffset runAfter, JobStatus status = JobStatus.Pending, int attemptCount = 0) => new()
    {
        Id = Guid.NewGuid(),
        TransactionId = transactionId,
        Status = status,
        AttemptCount = attemptCount,
        RunAfter = runAfter,
        CreatedAt = runAfter,
        UpdatedAt = runAfter,
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

        await queue.SucceedAsync(job.Id, TestContext.Current.CancellationToken);

        var reloaded = await db.CategorizationJobs.AsNoTracking()
            .SingleAsync(j => j.Id == job.Id, TestContext.Current.CancellationToken);
        reloaded.Status.Should().Be(JobStatus.Succeeded);
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

        await queue.RetryAsync(job.Id, nextRunAfter, "boom", TestContext.Current.CancellationToken);

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
        await queue.RetryAsync(job.Id, time.GetUtcNow().AddMinutes(1), "first failure", TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(2));
        await queue.ClaimAsync("worker-a", TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);

        await queue.RetryAsync(job.Id, time.GetUtcNow().AddMinutes(1), "second failure", TestContext.Current.CancellationToken);

        var reloaded = await db.CategorizationJobs.AsNoTracking()
            .SingleAsync(j => j.Id == job.Id, TestContext.Current.CancellationToken);
        reloaded.Status.Should().Be(JobStatus.Failed, "attempt 2 of a 2-attempt cap has no retries left");
        reloaded.LastError.Should().Be("second failure");
        reloaded.AttemptCount.Should().Be(2);
    }

    [Fact]
    public async Task Failing_a_job_marks_it_failed_unconditionally()
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

        await queue.FailAsync(job.Id, "not a transaction", TestContext.Current.CancellationToken);

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
}
