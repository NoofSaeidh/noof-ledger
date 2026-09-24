using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Configurations;
using Npgsql;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class VoiceSchemaTests(PostgresFixture fixture)
{
    static readonly Guid DefaultWalletId = new("00000000-0000-0000-0000-000000000001");
    static readonly DateTimeOffset Now = new(2026, 9, 24, 9, 0, 0, TimeSpan.Zero);
    static int nextMessageId = 1000;

    static Transaction NewTransaction(CaptureKind kind, string? rawText, string? voiceFileId) => new()
    {
        Id = Guid.NewGuid(),
        WalletId = DefaultWalletId,
        RawText = rawText,
        CaptureKind = kind,
        VoiceFileId = voiceFileId,
        VoiceDurationSeconds = voiceFileId is null ? null : 4,
        Status = TransactionStatus.Captured,
        TimeZoneId = "Europe/Belgrade",
        OccurredAt = Now,
        OccurredOn = new DateOnly(2026, 9, 24),
        TelegramChatId = 111,
        TelegramMessageId = Interlocked.Increment(ref nextMessageId),
        CreatedAt = Now,
    };

    static CategorizationJob NewJob(Guid transactionId, JobKind kind, int? sourceMessageId = null, string? voiceFileId = null,
        string? instruction = null) => new()
    {
        Id = Guid.NewGuid(),
        TransactionId = transactionId,
        Kind = kind,
        SourceMessageId = sourceMessageId,
        VoiceFileId = voiceFileId,
        Instruction = instruction,
        Status = JobStatus.Pending,
        AttemptCount = 0,
        RunAfter = Now,
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    static async Task<string?> ViolatedConstraintAsync(LedgerDbContext db)
    {
        try
        {
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return null;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException postgres)
        {
            return postgres.ConstraintName;
        }
    }

    [Fact]
    public async Task A_text_capture_without_text_is_refused()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        db.Transactions.Add(NewTransaction(CaptureKind.Text, rawText: null, voiceFileId: null));

        (await ViolatedConstraintAsync(db)).Should().Be("ck_transactions_capture_has_content");
    }

    [Fact]
    public async Task A_voice_capture_without_a_voice_file_is_refused()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        db.Transactions.Add(NewTransaction(CaptureKind.Voice, rawText: null, voiceFileId: null));

        (await ViolatedConstraintAsync(db)).Should().Be("ck_transactions_capture_has_content");
    }

    [Fact]
    public async Task A_voice_capture_awaiting_its_transcript_is_accepted()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = NewTransaction(CaptureKind.Voice, rawText: null, voiceFileId: "voice-file-1");
        db.Transactions.Add(transaction);

        (await ViolatedConstraintAsync(db)).Should().BeNull();

        db.ChangeTracker.Clear();
        var stored = await db.Transactions.SingleAsync(t => t.Id == transaction.Id, TestContext.Current.CancellationToken);
        stored.CaptureKind.Should().Be(CaptureKind.Voice);
        stored.RawText.Should().BeNull();
        stored.VoiceFileId.Should().Be("voice-file-1");
        stored.VoiceDurationSeconds.Should().Be(4);
    }

    [Fact]
    public async Task A_transcription_job_without_a_voice_file_is_refused()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = NewTransaction(CaptureKind.Voice, rawText: null, voiceFileId: "voice-file-1");
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.CategorizationJobs.Add(NewJob(transaction.Id, JobKind.Transcribe, voiceFileId: null));

        (await ViolatedConstraintAsync(db)).Should().Be("ck_categorization_jobs_transcription_has_voice_file");
    }

    [Fact]
    public async Task One_reply_may_queue_a_transcription_and_the_correction_it_produces()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = NewTransaction(CaptureKind.Text, rawText: "кофе 250", voiceFileId: null);
        db.Transactions.Add(transaction);
        db.CategorizationJobs.Add(NewJob(transaction.Id, JobKind.Transcribe, sourceMessageId: 900, voiceFileId: "reply-voice"));
        db.CategorizationJobs.Add(NewJob(transaction.Id, JobKind.Correct, sourceMessageId: 900, instruction: "нет, 1500"));

        (await ViolatedConstraintAsync(db)).Should().BeNull();
    }

    [Fact]
    public async Task The_same_reply_cannot_queue_two_transcriptions()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = NewTransaction(CaptureKind.Text, rawText: "кофе 250", voiceFileId: null);
        db.Transactions.Add(transaction);
        db.CategorizationJobs.Add(NewJob(transaction.Id, JobKind.Transcribe, sourceMessageId: 900, voiceFileId: "reply-voice"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.CategorizationJobs.Add(NewJob(transaction.Id, JobKind.Transcribe, sourceMessageId: 900, voiceFileId: "reply-voice"));

        (await ViolatedConstraintAsync(db)).Should().Be(CategorizationJobConfiguration.SourceMessageIndex);
    }
}
