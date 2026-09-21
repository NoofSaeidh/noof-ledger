using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Capture;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Capture;

public sealed class EfCaptureStore(LedgerDbContext db, TimeProvider timeProvider) : ICaptureStore
{
    public async Task<Guid> CaptureAsync(CapturedMessage message, string timeZoneId, CancellationToken cancellationToken)
    {
        var existing = await db.Transactions.SingleOrDefaultAsync(
            t => t.TelegramChatId == message.ChatId && t.TelegramMessageId == message.MessageId,
            cancellationToken);

        if (existing is not null)
            return existing.Id;

        // Checked before the wallet lookup: a replay of an already captured message must still
        // succeed even after the default wallet has been unmarked.
        var wallet = await db.Wallets.SingleOrDefaultAsync(w => w.IsDefault, cancellationToken)
            ?? throw new InvalidOperationException(
                "No wallet is marked as the default. Capture cannot proceed without one.");

        var now = timeProvider.GetUtcNow();
        var transactionId = Guid.NewGuid();

        db.Transactions.Add(new Transaction
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
        });

        db.CategorizationJobs.Add(new CategorizationJob
        {
            Id = Guid.NewGuid(),
            TransactionId = transactionId,
            Status = JobStatus.Pending,
            AttemptCount = 0,
            RunAfter = now,
            CreatedAt = now,
            UpdatedAt = now,
        });

        await db.SaveChangesAsync(cancellationToken);

        return transactionId;
    }

    public async Task AttachBotMessageAsync(Guid transactionId, int botMessageId, CancellationToken cancellationToken)
    {
        var transaction = await db.Transactions.SingleAsync(t => t.Id == transactionId, cancellationToken);
        transaction.BotMessageId = botMessageId;

        await db.SaveChangesAsync(cancellationToken);
    }
}
