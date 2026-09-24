using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Jobs;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Jobs;
using Noof.Ledger.Persistence.Transcription;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfTranscriptionStoreTests(PostgresFixture fixture)
{
    static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.Zero));
    static readonly Guid DefaultWalletId = new("00000000-0000-0000-0000-000000000001");

    static async Task<Transaction> SeedAsync(LedgerDbContext db, CaptureKind kind)
    {
        var transaction = new Transaction
        {
            Id = Guid.NewGuid(),
            WalletId = DefaultWalletId,
            RawText = kind == CaptureKind.Text ? "кофе 250" : null,
            CaptureKind = kind,
            VoiceFileId = kind == CaptureKind.Voice ? "voice-file-1" : null,
            VoiceDurationSeconds = kind == CaptureKind.Voice ? 4 : null,
            Status = TransactionStatus.Captured,
            TimeZoneId = "Europe/Belgrade",
            OccurredAt = new DateTimeOffset(2026, 9, 24, 8, 0, 0, TimeSpan.Zero),
            OccurredOn = new DateOnly(2026, 9, 24),
            TelegramChatId = 111,
            TelegramMessageId = 5,
            BotMessageId = 6,
            CreatedAt = new DateTimeOffset(2026, 9, 24, 8, 0, 0, TimeSpan.Zero),
        };
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        return transaction;
    }

    [Fact]
    public async Task Completing_a_capture_stores_the_transcript_and_queues_its_first_reading()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = await SeedAsync(db, CaptureKind.Voice);
        var store = new EfTranscriptionStore(db, Clock);

        var completed = await store.CompleteCaptureAsync(transaction.Id, "купил вчера штуку евро", TestContext.Current.CancellationToken);

        completed.Should().BeTrue();
        db.ChangeTracker.Clear();
        (await db.Transactions.SingleAsync(TestContext.Current.CancellationToken)).RawText.Should().Be("купил вчера штуку евро");
        var job = await db.CategorizationJobs.SingleAsync(TestContext.Current.CancellationToken);
        job.Kind.Should().Be(JobKind.Categorize);
        job.Status.Should().Be(JobStatus.Pending);
        job.SourceMessageId.Should().BeNull();
    }

    [Fact]
    public async Task Completing_a_capture_twice_queues_one_reading_and_keeps_the_first_transcript()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = await SeedAsync(db, CaptureKind.Voice);
        var store = new EfTranscriptionStore(db, Clock);

        await store.CompleteCaptureAsync(transaction.Id, "купил вчера штуку евро", TestContext.Current.CancellationToken);
        var again = await store.CompleteCaptureAsync(transaction.Id, "что-то другое", TestContext.Current.CancellationToken);

        again.Should().BeFalse("a Transcribe job re-run after its commit must not read the note twice");
        db.ChangeTracker.Clear();
        (await db.Transactions.SingleAsync(TestContext.Current.CancellationToken)).RawText.Should().Be("купил вчера штуку евро");
        (await db.CategorizationJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task Completing_a_spoken_correction_queues_a_correction_with_the_transcript_as_its_instruction()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = await SeedAsync(db, CaptureKind.Text);
        var store = new EfTranscriptionStore(db, Clock);

        var completed = await store.CompleteCorrectionAsync(
            transaction.Id, "нет, полторы тысячи", 900, new DateOnly(2026, 9, 25), TestContext.Current.CancellationToken);

        completed.Should().BeTrue();
        var job = await db.CategorizationJobs.SingleAsync(TestContext.Current.CancellationToken);
        job.Kind.Should().Be(JobKind.Correct);
        job.Instruction.Should().Be("нет, полторы тысячи");
        job.SourceMessageId.Should().Be(900);
        job.InstructionDay.Should().Be(new DateOnly(2026, 9, 25));
    }

    [Fact]
    public async Task Completing_the_same_spoken_correction_twice_queues_it_once()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = await SeedAsync(db, CaptureKind.Text);
        var store = new EfTranscriptionStore(db, Clock);

        await store.CompleteCorrectionAsync(transaction.Id, "нет, 1500", 900, null, TestContext.Current.CancellationToken);
        var again = await store.CompleteCorrectionAsync(transaction.Id, "нет, 1500", 900, null, TestContext.Current.CancellationToken);

        again.Should().BeFalse();
        (await db.CategorizationJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task A_correction_queued_while_transcription_is_still_pending_is_not_overwritten_by_the_reading_that_lands_after_it()
    {
        // Reproduces the reviewer's Important 1: a Correct job created after the Categorize job's
        // CreatedAt used to be treated as "earlier" by EfJobQueue.ClaimAsync's ordering rule and got
        // applied first, only for the Categorize job that lands afterwards to overwrite it. Giving
        // the Categorize job the capture's own position in the queue (the transaction's own
        // created_at, T0) keeps it strictly earlier than any correction queued after the capture.
        var t0 = new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.Zero);
        var t1 = t0.AddMinutes(1);
        var t2 = t0.AddMinutes(2);

        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var transaction = new Transaction
        {
            Id = Guid.NewGuid(),
            WalletId = DefaultWalletId,
            RawText = null,
            CaptureKind = CaptureKind.Voice,
            VoiceFileId = "voice-file-1",
            VoiceDurationSeconds = 4,
            Status = TransactionStatus.Captured,
            TimeZoneId = "Europe/Belgrade",
            OccurredAt = t0,
            OccurredOn = new DateOnly(2026, 9, 24),
            TelegramChatId = 111,
            TelegramMessageId = 5,
            BotMessageId = 6,
            CreatedAt = t0,
        };
        var transcribeJob = new CategorizationJob
        {
            Id = Guid.NewGuid(),
            TransactionId = transaction.Id,
            Kind = JobKind.Transcribe,
            VoiceFileId = "voice-file-1",
            Status = JobStatus.Claimed,
            AttemptCount = 1,
            ClaimedAt = t0,
            ClaimedBy = "worker-a",
            RunAfter = t0.AddMinutes(15),
            CreatedAt = t0,
            UpdatedAt = t0,
        };
        db.Transactions.Add(transaction);
        db.CategorizationJobs.Add(transcribeJob);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var correctJob = new CategorizationJob
        {
            Id = Guid.NewGuid(),
            TransactionId = transaction.Id,
            Kind = JobKind.Correct,
            Instruction = "нет, 1500",
            SourceMessageId = 900,
            Status = JobStatus.Pending,
            AttemptCount = 0,
            RunAfter = t1,
            CreatedAt = t1,
            UpdatedAt = t1,
        };
        db.CategorizationJobs.Add(correctJob);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var store = new EfTranscriptionStore(db, new FakeTimeProvider(t2));

        var completed = await store.CompleteCaptureAsync(transaction.Id, "купил вчера штуку евро", TestContext.Current.CancellationToken);

        completed.Should().BeTrue();
        db.ChangeTracker.Clear();
        var categorizeJob = await db.CategorizationJobs.SingleAsync(j => j.Kind == JobKind.Categorize, TestContext.Current.CancellationToken);
        categorizeJob.CreatedAt.Should().Be(t0, "the reading takes the capture's own place in the queue, not the moment transcription finished");

        var queue = new EfJobQueue(db, new FakeTimeProvider(t2), maxAttempts: 8);
        var succeeded = await queue.SucceedAsync(transcribeJob.Id, "worker-a", TestContext.Current.CancellationToken);
        succeeded.Should().Be(JobCompletionOutcome.Applied);

        JobKind[] kinds = [JobKind.Categorize, JobKind.Correct, JobKind.Reinterpret];
        var firstClaim = await queue.ClaimAsync("worker-b", kinds, TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);
        firstClaim.Should().NotBeNull();
        firstClaim!.Id.Should().Be(categorizeJob.Id, "the reading must be claimed and applied before the correction behind it");

        var secondClaim = await queue.ClaimAsync("worker-b", kinds, TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);
        secondClaim.Should().BeNull("the correction must wait until the reading it would otherwise undo is no longer pending or claimed");
    }

    [Fact]
    public async Task A_spoken_correction_may_share_its_reply_with_its_own_transcription_job()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = await SeedAsync(db, CaptureKind.Text);
        db.CategorizationJobs.Add(new CategorizationJob
        {
            Id = Guid.NewGuid(), TransactionId = transaction.Id, Kind = JobKind.Transcribe, VoiceFileId = "reply-voice",
            SourceMessageId = 900, Status = JobStatus.Claimed, AttemptCount = 1, RunAfter = Clock.GetUtcNow(),
            CreatedAt = Clock.GetUtcNow().AddMinutes(-1), UpdatedAt = Clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var store = new EfTranscriptionStore(db, Clock);

        var completed = await store.CompleteCorrectionAsync(transaction.Id, "нет, 1500", 900, null, TestContext.Current.CancellationToken);

        completed.Should().BeTrue("the unique key includes the kind");
    }
}
