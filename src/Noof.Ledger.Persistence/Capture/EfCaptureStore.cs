using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Capture;
using Noof.Ledger.Domain;
using Npgsql;

namespace Noof.Ledger.Persistence.Capture;

internal sealed class EfCaptureStore(LedgerDbContext db, TimeProvider timeProvider) : ICaptureStore
{
    public Task<Guid> CaptureAsync(CapturedMessage message, string timeZoneId, CancellationToken cancellationToken) =>
        StoreAsync(message.ChatId, message.MessageId, message.SentAt, timeZoneId, message.Text, voice: null, cancellationToken);

    public Task<Guid> CaptureVoiceAsync(CapturedVoice voice, string timeZoneId, CancellationToken cancellationToken) =>
        StoreAsync(voice.ChatId, voice.MessageId, voice.SentAt, timeZoneId, rawText: null, voice, cancellationToken);

    async Task<Guid> StoreAsync(
        long chatId, int messageId, DateTimeOffset sentAt, string timeZoneId, string? rawText, CapturedVoice? voice,
        CancellationToken cancellationToken)
    {
        var existing = await FindExistingAsync(chatId, messageId, cancellationToken);

        if (existing is not null)
            return existing.Id;

        var now = timeProvider.GetUtcNow();
        var transactionId = Guid.NewGuid();

        var transaction = new Transaction
        {
            Id = transactionId,
            RawText = rawText,
            CaptureKind = voice is null ? CaptureKind.Text : CaptureKind.Voice,
            VoiceFileId = voice?.VoiceFileId,
            VoiceDurationSeconds = voice?.DurationSeconds,
            Status = TransactionStatus.Captured,
            TimeZoneId = timeZoneId,
            OccurredAt = sentAt,
            OccurredOn = ZonedClock.LocalDate(sentAt, timeZoneId),
            TelegramChatId = chatId,
            TelegramMessageId = messageId,
            CreatedAt = now,
        };
        var job = new CategorizationJob
        {
            Id = Guid.NewGuid(),
            TransactionId = transactionId,
            Kind = voice is null ? JobKind.Categorize : JobKind.Transcribe,
            VoiceFileId = voice?.VoiceFileId,
            Status = JobStatus.Pending,
            AttemptCount = 0,
            RunAfter = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Transactions.Add(transaction);
        db.CategorizationJobs.Add(job);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return transactionId;
        }
        catch (DbUpdateException ex) when (IsDuplicateCaptureViolation(ex))
        {
            // Another concurrent call for the same (ChatId, MessageId) committed first. Ours never
            // did; detach both rows so the re-read below goes back to the database instead of
            // returning these uncommitted, never-persisted entities from the identity map.
            db.Entry(transaction).State = EntityState.Detached;
            db.Entry(job).State = EntityState.Detached;

            var winner = await FindExistingAsync(chatId, messageId, cancellationToken)
                ?? throw new InvalidOperationException(
                    "A unique-constraint violation on capture reported a winner that cannot be found.");
            return winner.Id;
        }
    }

    Task<Transaction?> FindExistingAsync(long chatId, int messageId, CancellationToken cancellationToken) =>
        db.Transactions.SingleOrDefaultAsync(
            t => t.TelegramChatId == chatId && t.TelegramMessageId == messageId,
            cancellationToken);

    static bool IsDuplicateCaptureViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "IX_transactions_telegram_chat_id_telegram_message_id",
        };

    public async Task AttachBotMessageAsync(Guid transactionId, int botMessageId, CancellationToken cancellationToken)
    {
        var transaction = await db.Transactions.SingleAsync(t => t.Id == transactionId, cancellationToken);
        transaction.BotMessageId = botMessageId;

        await db.SaveChangesAsync(cancellationToken);
    }
}
