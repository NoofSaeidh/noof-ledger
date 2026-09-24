using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Transcription;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Configurations;
using Npgsql;

namespace Noof.Ledger.Persistence.Transcription;

internal sealed class EfTranscriptionStore(LedgerDbContext db, TimeProvider timeProvider) : ITranscriptionStore
{
    public async Task<bool> CompleteCaptureAsync(Guid transactionId, string transcript, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Only a record still without text takes a transcript: the condition is what makes a re-run of the same
        // Transcribe job, after this commit landed and its SucceedAsync did not, a no-op.
        var capturedAt = await db.Database.SqlQueryRaw<DateTimeOffset>(
                "UPDATE transactions SET raw_text = @transcript WHERE id = @transactionId AND raw_text IS NULL RETURNING created_at",
                new NpgsqlParameter("transcript", transcript), new NpgsqlParameter("transactionId", transactionId))
            .ToListAsync(cancellationToken);

        if (capturedAt.Count == 0)
            return false;

        // The Categorize job takes the capture's own position in the queue (the transaction's own
        // created_at), not the moment transcription happened to finish. EfJobQueue.ClaimAsync orders
        // a transaction's jobs by created_at, so a Categorize job stamped "now" could land after a
        // Correct job queued while transcription was still pending - the correction would then be
        // claimed and applied first, only for this reading to overwrite it once it arrives.
        db.CategorizationJobs.Add(NewJob(
            transactionId, JobKind.Categorize, instruction: null, sourceMessageId: null, instructionDay: null, createdAt: capturedAt[0]));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> CompleteCorrectionAsync(
        Guid transactionId, string transcript, int sourceMessageId, DateOnly? instructionDay, CancellationToken cancellationToken)
    {
        var job = NewJob(transactionId, JobKind.Correct, transcript, sourceMessageId, instructionDay);
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

    CategorizationJob NewJob(
        Guid transactionId, JobKind kind, string? instruction, int? sourceMessageId, DateOnly? instructionDay,
        DateTimeOffset? createdAt = null)
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
            Status = JobStatus.Pending,
            AttemptCount = 0,
            RunAfter = now,
            CreatedAt = createdAt ?? now,
            UpdatedAt = now,
        };
    }
}
