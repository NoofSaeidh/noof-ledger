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

    // A correction always queues Correct, whatever the transaction turns out to be. Whether it
    // belongs to a receipt (and so must go to CategorizeReceipt instead of record_transaction) is
    // decided once, at claim time, by CategorizationWorker.TryRouteToReceiptAsync (ruling F-2, Phase 6
    // re-review) - not here, and not in EfTranscriptionStore or ReplaceRawTextAsync either. Deciding
    // it at every enqueue site was exactly what let a voice correction (N-1) and an edited message
    // (N-2) skip the rule a typed reply already followed.
    public async Task<bool> RequestCorrectionAsync(
        Guid transactionId, string instruction, int sourceMessageId, DateTimeOffset sentAt, CancellationToken cancellationToken)
    {
        var instructionDay = await InstructionDayAsync(transactionId, sentAt, cancellationToken);
        return await TryQueueReplyAsync(
            transactionId, () => NewJob(transactionId, JobKind.Correct, instruction, sourceMessageId, instructionDay),
            cancellationToken);
    }

    public async Task<bool> RequestVoiceCorrectionAsync(
        Guid transactionId, string voiceFileId, int sourceMessageId, DateTimeOffset sentAt, CancellationToken cancellationToken)
    {
        var instructionDay = await InstructionDayAsync(transactionId, sentAt, cancellationToken);
        return await TryQueueReplyAsync(
            transactionId, () => NewJob(transactionId, JobKind.Transcribe, instruction: null, sourceMessageId, instructionDay, voiceFileId),
            cancellationToken);
    }

    // sentAt is the correction reply's own send instant, so the model's "today" for this correction is the
    // reply's local day, not the original message's (docs/decisions/p2-2-correction-today-anchor.md).
    async Task<DateOnly?> InstructionDayAsync(Guid transactionId, DateTimeOffset sentAt, CancellationToken cancellationToken)
    {
        var timeZoneId = await db.Transactions.AsNoTracking()
            .Where(t => t.Id == transactionId)
            .Select(t => (string?)t.TimeZoneId)
            .SingleOrDefaultAsync(cancellationToken);

        return timeZoneId is null ? null : ZonedClock.LocalDate(sentAt, timeZoneId);
    }

    // False when this exact reply is already queued: Telegram redelivers an update whose handling failed partway.
    // The job is stamped under the row lock a slip save and Record anyway check for a reply under: stamped before it,
    // a reply committed after their check would still be claimed ahead of the caption they queued.
    async Task<bool> TryQueueReplyAsync(Guid transactionId, Func<CategorizationJob> newJob, CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockRowAsync(transactionId, cancellationToken);
        var job = newJob();
        db.CategorizationJobs.Add(job);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
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
        var cancel = await db.TransactionRevisions
            .Where(r => r.TransactionId == transactionId && r.Kind == RevisionKind.Cancel)
            .OrderByDescending(r => r.RevisionNumber)
            .Select(r => new { r.RevisionNumber, r.StatusBefore })
            .FirstAsync(cancellationToken);

        // ApplyAsync completes a record it applies while cancelled but leaves it cancelled. Putting back the status from
        // before the cancel would then restore a Captured or Failed that no longer describes it (A-22).
        var appliedSinceCancel = await db.TransactionRevisions.AnyAsync(
            r => r.TransactionId == transactionId
                && r.RevisionNumber > cancel.RevisionNumber
                && (r.Kind == RevisionKind.Initial || r.Kind == RevisionKind.Correction || r.Kind == RevisionKind.Edit),
            cancellationToken);

        transaction.Status = appliedSinceCancel ? TransactionStatus.Completed : cancel.StatusBefore;
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
        await LockRowAsync(transactionId, cancellationToken);
        return await db.Transactions.SingleOrDefaultAsync(t => t.Id == transactionId, cancellationToken);
    }

    Task LockRowAsync(Guid transactionId, CancellationToken cancellationToken) =>
        db.Database.SqlQueryRaw<Guid>(
            "SELECT id FROM transactions WHERE id = @transactionId FOR UPDATE",
            new NpgsqlParameter("transactionId", transactionId))
            .ToListAsync(cancellationToken);
}
