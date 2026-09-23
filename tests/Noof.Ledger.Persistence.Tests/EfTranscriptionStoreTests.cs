using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Domain;
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
