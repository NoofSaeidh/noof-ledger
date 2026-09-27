using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Configurations;
using Npgsql;
using AppReceipts = Noof.Ledger.Application.Receipts;

namespace Noof.Ledger.Persistence.Receipts;

internal sealed class EfReceiptStore(LedgerDbContext db, TimeProvider timeProvider) : AppReceipts.IReceiptStore
{
    public async Task<AppReceipts.ReceiptSaveResult> SaveExtractedAsync(
        Guid transactionId, AppReceipts.ExtractedReceipt receipt, string? telegramFileId, bool enqueueCategorization,
        CancellationToken cancellationToken)
    {
        var receiptId = Guid.NewGuid();

        var row = new Receipt
        {
            Id = receiptId,
            TransactionId = transactionId,
            Source = receipt.Source,
            VerificationUrl = receipt.VerificationUrl,
            SellerTaxId = receipt.SellerTaxId,
            SellerName = receipt.SellerName,
            SellerAddress = receipt.SellerAddress,
            LocationName = receipt.LocationName,
            FiscalNumber = receipt.FiscalNumber,
            // Npgsql writes timestamptz only from an offset-0 value; the parsers stamp Belgrade's offset.
            IssuedAt = receipt.IssuedAt?.ToUniversalTime(),
            Total = new Money(receipt.Total, receipt.Currency),
            Kind = receipt.Kind,
            PaymentMethod = receipt.PaymentMethod,
            QrTotal = receipt.QrTotal,
            TelegramFileId = telegramFileId,
            CreatedAt = timeProvider.GetUtcNow(),
        };

        db.Receipts.Add(row);

        foreach (var line in receipt.Lines)
        {
            db.ReceiptLines.Add(new ReceiptLine
            {
                Id = Guid.NewGuid(),
                ReceiptId = receiptId,
                Ordinal = line.Ordinal,
                Name = line.Name,
                Quantity = line.Quantity,
                Unit = line.Unit,
                UnitPrice = line.UnitPrice,
                Total = line.Total,
                TaxLabel = line.TaxLabel,
            });
        }

        // Enqueued in the same SaveChangesAsync as the receipt and its lines: a duplicate violation
        // rolls this job back with everything else, so a duplicate transaction never gets a
        // CategorizeReceipt job of its own. Skipped entirely when the caller judged the receipt not to
        // add up (2026-09-27) - the receipt and its lines are still saved below, but categorisation
        // waits for the operator's own EnqueueCategorizationAsync ("Record anyway").
        var now = timeProvider.GetUtcNow();
        if (enqueueCategorization)
        {
            db.CategorizationJobs.Add(new CategorizationJob
            {
                Id = Guid.NewGuid(),
                TransactionId = transactionId,
                Kind = JobKind.CategorizeReceipt,
                Status = JobStatus.Pending,
                AttemptCount = 0,
                RunAfter = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new AppReceipts.ReceiptSaveResult(receiptId, null);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, TransactionIndex) || IsUniqueViolation(ex, ReceiptConfiguration.DuplicateIndex))
        {
            db.ChangeTracker.Clear();

            // A job re-run (its lease expired between the commit below and the caller succeeding the
            // job, C-1) lands here first: the receipt for THIS transaction already exists, whichever
            // index caught it - IX_receipts_transaction_id for a vision receipt (no fiscal number to
            // collide on), or the seller+fiscal index when the same fiscal receipt is replayed for the
            // same transaction. Either way it is the caller's own earlier save, never a duplicate of
            // itself, so this returns success with the existing id instead of falling through to the
            // cross-transaction duplicate lookup below.
            var ownReceiptId = await db.Receipts.AsNoTracking()
                .Where(r => r.TransactionId == transactionId)
                .Select(r => (Guid?)r.Id)
                .SingleOrDefaultAsync(cancellationToken);

            if (ownReceiptId is { } existingReceiptId)
                return new AppReceipts.ReceiptSaveResult(existingReceiptId, null);

            var duplicateOf = await db.Receipts.AsNoTracking()
                .Where(r => r.SellerTaxId == receipt.SellerTaxId && r.FiscalNumber == receipt.FiscalNumber)
                .Select(r => (Guid?)r.TransactionId)
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException(
                    "A unique-constraint violation on receipts reported a duplicate that cannot be found.");

            return new AppReceipts.ReceiptSaveResult(null, duplicateOf);
        }
    }

    public async Task<AppReceipts.ReceiptView?> GetByTransactionAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var receipt = await db.Receipts.AsNoTracking()
            .SingleOrDefaultAsync(r => r.TransactionId == transactionId, cancellationToken);

        if (receipt is null)
            return null;

        var lines = await db.ReceiptLines.AsNoTracking()
            .Where(l => l.ReceiptId == receipt.Id)
            .OrderBy(l => l.Ordinal)
            .Select(l => new AppReceipts.ReceiptLineView(l.Id, l.Ordinal, l.Name, l.Quantity, l.Unit, l.UnitPrice, l.Total, l.TaxLabel))
            .ToListAsync(cancellationToken);

        return new AppReceipts.ReceiptView(
            receipt.Id,
            receipt.Source,
            receipt.SellerTaxId,
            receipt.SellerName,
            receipt.SellerAddress,
            receipt.LocationName,
            receipt.FiscalNumber,
            receipt.IssuedAt,
            receipt.Total.Amount,
            receipt.Total.Currency,
            receipt.Kind,
            receipt.PaymentMethod,
            receipt.QrTotal,
            receipt.VerificationUrl,
            lines);
    }

    public Task<string?> GetTelegramFileIdAsync(Guid transactionId, CancellationToken cancellationToken) =>
        db.Transactions.AsNoTracking()
            .Where(t => t.Id == transactionId)
            .Select(t => t.TelegramFileId)
            .SingleOrDefaultAsync(cancellationToken);

    public Task<string?> GetVerificationUrlAsync(Guid transactionId, CancellationToken cancellationToken) =>
        db.Transactions.AsNoTracking()
            .Where(t => t.Id == transactionId)
            .Select(t => t.VerificationUrl)
            .SingleOrDefaultAsync(cancellationToken);

    // Row lock, same pattern and reason as EfRecordEditor.LockAsync / EfCategorizationStore.ApplyAsync:
    // without it, the status/awaiting-confirmation reads that decide whether to insert are not atomic
    // with a concurrent Cancel. A Cancel that commits between an unlocked read and this insert would
    // otherwise still get a CategorizeReceipt job queued for it - the receipt worker can then apply
    // line items while the transaction itself stays Cancelled. FOR UPDATE makes a concurrent Cancel
    // (which takes the same lock in EfRecordEditor) block until this transaction commits or rolls
    // back, so the status this reads is never stale by the time the insert happens.
    public async Task<bool> EnqueueCategorizationAsync(Guid transactionId, int sourceMessageId, CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.Database.SqlQueryRaw<Guid>(
            "SELECT id FROM transactions WHERE id = @transactionId FOR UPDATE",
            new NpgsqlParameter("transactionId", transactionId))
            .ToListAsync(cancellationToken);

        var status = await db.Transactions.AsNoTracking()
            .Where(t => t.Id == transactionId)
            .Select(t => (TransactionStatus?)t.Status)
            .SingleOrDefaultAsync(cancellationToken);

        if (status != TransactionStatus.Captured || !await IsAwaitingConfirmationAsync(transactionId, cancellationToken))
            return false;

        var now = timeProvider.GetUtcNow();
        db.CategorizationJobs.Add(new CategorizationJob
        {
            Id = Guid.NewGuid(),
            TransactionId = transactionId,
            Kind = JobKind.CategorizeReceipt,
            Status = JobStatus.Pending,
            AttemptCount = 0,
            RunAfter = now,
            CreatedAt = now,
            UpdatedAt = now,
            SourceMessageId = sourceMessageId,
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, CategorizationJobConfiguration.SourceMessageIndex))
        {
            db.ChangeTracker.Clear();
            return false;
        }

        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> IsAwaitingConfirmationAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var source = await db.Receipts.AsNoTracking()
            .Where(r => r.TransactionId == transactionId)
            .Select(r => (ReceiptSource?)r.Source)
            .SingleOrDefaultAsync(cancellationToken);

        if (source != ReceiptSource.Vision)
            return false;

        return !await db.CategorizationJobs.AsNoTracking()
            .AnyAsync(job => job.TransactionId == transactionId && job.Kind == JobKind.CategorizeReceipt, cancellationToken);
    }

    // EF's default naming for the one-to-one FK's auto-generated unique index (ReceiptConfiguration
    // never names it explicitly) - confirmed against the migration, not guessed.
    const string TransactionIndex = "IX_receipts_transaction_id";

    static bool IsUniqueViolation(DbUpdateException ex, string constraintName) =>
        ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
        } pg
        && pg.ConstraintName == constraintName;
}
