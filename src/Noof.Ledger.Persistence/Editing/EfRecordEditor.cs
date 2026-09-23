using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Editing;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Configurations;
using Noof.Ledger.Persistence.Revisions;
using Npgsql;

namespace Noof.Ledger.Persistence.Editing;

internal sealed class EfRecordEditor(LedgerDbContext db, TimeProvider timeProvider) : IRecordEditor
{
    public Task<EchoTarget?> FindByBotMessageAsync(long chatId, int messageId, CancellationToken cancellationToken) =>
        db.Transactions.AsNoTracking()
            .Where(t => t.TelegramChatId == chatId
                && t.BotMessageId != null
                && (t.BotMessageId == messageId || t.PromptMessageId == messageId))
            .Select(t => new EchoTarget(t.Id, t.BotMessageId))
            .SingleOrDefaultAsync(cancellationToken);

    public Task<EchoTarget?> FindByUserMessageAsync(long chatId, int messageId, CancellationToken cancellationToken) =>
        db.Transactions.AsNoTracking()
            .Where(t => t.TelegramChatId == chatId && t.TelegramMessageId == messageId)
            .Select(t => new EchoTarget(t.Id, t.BotMessageId))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<bool> RequestCorrectionAsync(
        Guid transactionId, string instruction, int sourceMessageId, DateTimeOffset sentAt, CancellationToken cancellationToken) =>
        await TryQueueAsync(
            NewJob(transactionId, JobKind.Correct, instruction, sourceMessageId,
                await InstructionDayAsync(transactionId, sentAt, cancellationToken)),
            cancellationToken);

    public async Task<bool> RequestVoiceCorrectionAsync(
        Guid transactionId, string voiceFileId, int sourceMessageId, DateTimeOffset sentAt, CancellationToken cancellationToken) =>
        await TryQueueAsync(
            NewJob(transactionId, JobKind.Transcribe, instruction: null, sourceMessageId,
                await InstructionDayAsync(transactionId, sentAt, cancellationToken), voiceFileId),
            cancellationToken);

    // sentAt is the correction reply's own send instant, so the model's "today" for this correction is the
    // reply's local day, not the original message's (docs/OPEN-QUESTIONS.md P2-2).
    async Task<DateOnly?> InstructionDayAsync(Guid transactionId, DateTimeOffset sentAt, CancellationToken cancellationToken)
    {
        var timeZoneId = await db.Transactions.AsNoTracking()
            .Where(t => t.Id == transactionId)
            .Select(t => (string?)t.TimeZoneId)
            .SingleOrDefaultAsync(cancellationToken);

        return timeZoneId is null ? null : ZonedClock.LocalDate(sentAt, timeZoneId);
    }

    // False when this exact reply is already queued: Telegram redelivers an update whose handling failed partway.
    async Task<bool> TryQueueAsync(CategorizationJob job, CancellationToken cancellationToken)
    {
        db.CategorizationJobs.Add(job);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: CategorizationJobConfiguration.SourceMessageIndex,
        })
        {
            db.Entry(job).State = EntityState.Detached;
            return false;
        }
    }

    public async Task<bool> ReplaceRawTextAsync(Guid transactionId, string rawText, CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await LockAsync(transactionId, cancellationToken) is not { } transaction
            || string.Equals(transaction.RawText, rawText, StringComparison.Ordinal))
            return false;

        transaction.RawText = rawText;
        db.CategorizationJobs.Add(NewJob(transactionId, JobKind.Reinterpret, instruction: null, sourceMessageId: null, instructionDay: null));
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task AttachPromptAsync(Guid transactionId, int promptMessageId, CancellationToken cancellationToken)
    {
        var transaction = await db.Transactions.SingleAsync(t => t.Id == transactionId, cancellationToken);
        transaction.PromptMessageId = promptMessageId;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> CancelAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await LockAsync(transactionId, cancellationToken) is not { } transaction
            || transaction.Status == TransactionStatus.Cancelled)
            return false;

        var statusBefore = transaction.Status;
        transaction.Status = TransactionStatus.Cancelled;
        await db.SaveChangesAsync(cancellationToken);
        await RevisionLog.AppendAsync(db, transaction, RevisionKind.Cancel, null, statusBefore, timeProvider.GetUtcNow(), cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RestoreAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await LockAsync(transactionId, cancellationToken) is not { Status: TransactionStatus.Cancelled } transaction)
            return false;

        // CancelAsync is the only way a record becomes Cancelled, and it always writes this revision.
        var statusBeforeCancel = await db.TransactionRevisions
            .Where(r => r.TransactionId == transactionId && r.Kind == RevisionKind.Cancel)
            .OrderByDescending(r => r.RevisionNumber)
            .Select(r => r.StatusBefore)
            .FirstAsync(cancellationToken);

        transaction.Status = statusBeforeCancel;
        await db.SaveChangesAsync(cancellationToken);
        await RevisionLog.AppendAsync(db, transaction, RevisionKind.Restore, null, TransactionStatus.Cancelled,
            timeProvider.GetUtcNow(), cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return true;
    }

    CategorizationJob NewJob(
        Guid transactionId, JobKind kind, string? instruction, int? sourceMessageId, DateOnly? instructionDay,
        string? voiceFileId = null)
    {
        var now = timeProvider.GetUtcNow();
        return new CategorizationJob
        {
            Id = Guid.NewGuid(),
            TransactionId = transactionId,
            Kind = kind,
            Instruction = instruction,
            SourceMessageId = sourceMessageId,
            InstructionDay = instructionDay,
            VoiceFileId = voiceFileId,
            Status = JobStatus.Pending,
            AttemptCount = 0,
            RunAfter = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    // The same row lock EfCategorizationStore.ApplyAsync takes, so a button press and a worker writing the
    // same record serialise instead of interleaving their revisions.
    async Task<Transaction?> LockAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        await db.Database.SqlQueryRaw<Guid>(
            "SELECT id FROM transactions WHERE id = @transactionId FOR UPDATE",
            new NpgsqlParameter("transactionId", transactionId))
            .ToListAsync(cancellationToken);

        return await db.Transactions.SingleOrDefaultAsync(t => t.Id == transactionId, cancellationToken);
    }
}
