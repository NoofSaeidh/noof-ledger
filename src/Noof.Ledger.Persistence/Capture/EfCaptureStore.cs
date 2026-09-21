using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Capture;
using Noof.Ledger.Domain;
using Npgsql;

namespace Noof.Ledger.Persistence.Capture;

public sealed class EfCaptureStore(LedgerDbContext db, TimeProvider timeProvider) : ICaptureStore
{
    public async Task<Guid> CaptureAsync(CapturedMessage message, string timeZoneId, CancellationToken cancellationToken)
    {
        var existing = await FindExistingAsync(message, cancellationToken);

        if (existing is not null)
            return existing.Id;

        // Checked before the wallet lookup: a replay of an already captured message must still
        // succeed even after the default wallet has been unmarked.
        var wallet = await db.Wallets.SingleOrDefaultAsync(w => w.IsDefault, cancellationToken)
            ?? throw new InvalidOperationException(
                "No wallet is marked as the default. Capture cannot proceed without one.");

        var now = timeProvider.GetUtcNow();
        var transactionId = Guid.NewGuid();

        var transaction = new Transaction
        {
            Id = transactionId,
            WalletId = wallet.Id,
            RawText = message.Text,
            Status = TransactionStatus.Captured,
            TimeZoneId = timeZoneId,
            OccurredAt = message.SentAt,
            TelegramChatId = message.ChatId,
            TelegramMessageId = message.MessageId,
            CreatedAt = now,
        };
        var job = new CategorizationJob
        {
            Id = Guid.NewGuid(),
            TransactionId = transactionId,
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

            var winner = await FindExistingAsync(message, cancellationToken)
                ?? throw new InvalidOperationException(
                    "A unique-constraint violation on capture reported a winner that cannot be found.");
            return winner.Id;
        }
    }

    Task<Transaction?> FindExistingAsync(CapturedMessage message, CancellationToken cancellationToken) =>
        db.Transactions.SingleOrDefaultAsync(
            t => t.TelegramChatId == message.ChatId && t.TelegramMessageId == message.MessageId,
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
