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
        var updated = await db.Database.ExecuteSqlRawAsync(
            "UPDATE transactions SET raw_text = @transcript WHERE id = @transactionId AND raw_text IS NULL",
            [new NpgsqlParameter("transcript", transcript), new NpgsqlParameter("transactionId", transactionId)],
            cancellationToken);

        if (updated == 0)
            return false;

        db.CategorizationJobs.Add(NewJob(transactionId, JobKind.Categorize, instruction: null, sourceMessageId: null, instructionDay: null));
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

    CategorizationJob NewJob(Guid transactionId, JobKind kind, string? instruction, int? sourceMessageId, DateOnly? instructionDay)
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
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
