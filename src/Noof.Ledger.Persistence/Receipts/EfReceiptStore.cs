using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Configurations;
using Noof.Ledger.Persistence.Revisions;
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
            if (await OwnReceiptIdAsync(transactionId, cancellationToken) is { } existingReceiptId)
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

    // Evidence and its disposition in ONE database transaction (spec §3), under the row lock a Cancel also
    // takes: a crash never leaves a saved slip with no job, or a job for a slip that was never saved.
    public async Task<AppReceipts.ReceiptSaveResult> SaveExchangeSlipAsync(
        Guid transactionId, AppReceipts.ExtractedReceipt receipt, AppReceipts.ExtractedExchange exchange, string? telegramFileId,
        AppReceipts.SlipDisposition disposition, CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var transaction = await LockAsync(transactionId, cancellationToken)
            ?? throw new InvalidOperationException($"There is no transaction {transactionId} to save a slip for.");

        var receiptId = Guid.NewGuid();
        var slipNumber = NormalisedSlipNumber(exchange.SlipNumber);
        var now = timeProvider.GetUtcNow();

        db.Receipts.Add(new Receipt
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
            Kind = ReceiptKind.Exchange,
            PaymentMethod = receipt.PaymentMethod,
            QrTotal = receipt.QrTotal,
            TelegramFileId = telegramFileId,
            SlipNumber = slipNumber,
            CreatedAt = now,
        });

        db.ReceiptExchanges.Add(new ReceiptExchange
        {
            ReceiptId = receiptId,
            GivenAmount = exchange.GivenAmount,
            GivenCurrency = exchange.GivenCurrency,
            ReceivedAmount = exchange.ReceivedAmount,
            ReceivedCurrency = exchange.ReceivedCurrency,
            Rate = exchange.Rate,
            CommissionAmount = exchange.CommissionAmount,
            CommissionCurrency = exchange.CommissionCurrency,
            SlipNumber = exchange.SlipNumber,
        });

        if (disposition == AppReceipts.SlipDisposition.Record)
            db.CategorizationJobs.Add(PendingJob(transactionId, JobKind.RecordExchange, now));

        // Amendment 26: the slip's issue date is the record's date until a correction says otherwise.
        if (receipt.IssuedAt is { } issuedAt)
        {
            var slipDay = ZonedClock.LocalDate(issuedAt, transaction.TimeZoneId);
            await db.Transactions
                .Where(t => t.Id == transactionId)
                .ExecuteUpdateAsync(set => set.SetProperty(t => t.OccurredOn, slipDay), cancellationToken);
        }

        if (disposition == AppReceipts.SlipDisposition.Incomplete)
        {
            await db.Transactions
                .Where(t => t.Id == transactionId && t.Status == TransactionStatus.Captured)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(t => t.Status, TransactionStatus.Failed)
                    .SetProperty(t => t.FailureReason, RecordFailureReason.SlipIncomplete),
                    cancellationToken);
        }

        // A held slip's caption waits for "Record anyway" (EnqueueCategorizationAsync); an incomplete one's goes
        // at once, since it may carry the missing figure. A-22: a reply sent while the photo was being read is the
        // operator's later word, and the caption queued behind it would re-apply the older figures over it.
        if (disposition != AppReceipts.SlipDisposition.Hold
            && CaptionJob(transactionId, transaction, now) is { } caption
            && !await HasReplyInFlightAsync(transactionId, cancellationToken))
            db.CategorizationJobs.Add(caption);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return new AppReceipts.ReceiptSaveResult(receiptId, null);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, TransactionIndex) || IsUniqueViolation(ex, ReceiptConfiguration.SlipDuplicateIndex))
        {
            await tx.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return await SlipSaveConflictAsync(transactionId, receipt.SellerTaxId, slipNumber, cancellationToken);
        }
    }

    public Task<AppReceipts.ExchangeSlipView?> GetExchangeSlipAsync(Guid transactionId, CancellationToken cancellationToken) =>
        (from receipt in db.Receipts.AsNoTracking()
         where receipt.TransactionId == transactionId && receipt.Kind == ReceiptKind.Exchange
         join evidence in db.ReceiptExchanges.AsNoTracking() on receipt.Id equals evidence.ReceiptId
         select new AppReceipts.ExchangeSlipView(
             receipt.Id, receipt.SellerTaxId, receipt.SellerName, receipt.IssuedAt, receipt.SlipNumber,
             new AppReceipts.ExtractedExchange(
                 evidence.GivenAmount, evidence.GivenCurrency, evidence.ReceivedAmount, evidence.ReceivedCurrency,
                 evidence.Rate, evidence.CommissionAmount, evidence.CommissionCurrency, evidence.SlipNumber)))
        .SingleOrDefaultAsync(cancellationToken);

    // A replay of this transaction's own save (C-1) returns the receipt it already has; anything else is a slip
    // another transaction already recorded - the same two answers SaveExtractedAsync gives a fiscal receipt.
    async Task<AppReceipts.ReceiptSaveResult> SlipSaveConflictAsync(
        Guid transactionId, string? sellerTaxId, string? slipNumber, CancellationToken cancellationToken)
    {
        if (await OwnReceiptIdAsync(transactionId, cancellationToken) is { } existingReceiptId)
            return new AppReceipts.ReceiptSaveResult(existingReceiptId, null);

        var duplicateOf = await db.Receipts.AsNoTracking()
            .Where(r => r.Kind == ReceiptKind.Exchange && r.SellerTaxId == sellerTaxId && r.SlipNumber == slipNumber)
            .Select(r => (Guid?)r.TransactionId)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "A unique-constraint violation on receipts reported a duplicate slip that cannot be found.");

        return new AppReceipts.ReceiptSaveResult(null, duplicateOf);
    }

    Task<Guid?> OwnReceiptIdAsync(Guid transactionId, CancellationToken cancellationToken) =>
        db.Receipts.AsNoTracking()
            .Where(r => r.TransactionId == transactionId)
            .Select(r => (Guid?)r.Id)
            .SingleOrDefaultAsync(cancellationToken);

    sealed record LockedTransaction(TransactionStatus Status, string? RawText, DateTimeOffset OccurredAt, string TimeZoneId);

    // The row lock EfRecordEditor and EfCategorizationStore.ApplyAsync also take, so a Cancel pressed meanwhile
    // waits instead of interleaving. Read untracked: a tracked copy from earlier in this context's life could be
    // stale.
    async Task<LockedTransaction?> LockAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        await db.Database.SqlQueryRaw<Guid>(
            "SELECT id FROM transactions WHERE id = @transactionId FOR UPDATE",
            new NpgsqlParameter("transactionId", transactionId))
            .ToListAsync(cancellationToken);

        return await db.Transactions.AsNoTracking()
            .Where(t => t.Id == transactionId)
            .Select(t => new LockedTransaction(t.Status, t.RawText, t.OccurredAt, t.TimeZoneId))
            .SingleOrDefaultAsync(cancellationToken);
    }

    static CategorizationJob PendingJob(
        Guid transactionId, JobKind kind, DateTimeOffset now, int? sourceMessageId = null, string? instruction = null,
        DateOnly? instructionDay = null, DateTimeOffset? createdAt = null) => new()
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

    const long CaptionDelayTicks = 10;

    // A slip photo's caption is the operator's own word on it, applied as a correction after RecordExchange
    // (spec §3) and dated the day the photo was sent, when the caption was written. One microsecond later than
    // the job it follows (amendment 11): the claim query orders one transaction's jobs by a strict
    // created_at <, and timestamptz keeps microseconds. Only created_at moves - a later run_after would make it
    // look "not yet due" instead of "waiting its turn".
    static CategorizationJob? CaptionJob(Guid transactionId, LockedTransaction transaction, DateTimeOffset now) =>
        string.IsNullOrWhiteSpace(transaction.RawText)
            ? null
            : PendingJob(transactionId, JobKind.Correct, now,
                instruction: transaction.RawText.Trim(),
                instructionDay: ZonedClock.LocalDate(transaction.OccurredAt, transaction.TimeZoneId),
                createdAt: now.AddTicks(CaptionDelayTicks));

    internal static string? NormalisedSlipNumber(string? slipNumber) =>
        string.IsNullOrWhiteSpace(slipNumber) ? null : slipNumber.Trim().ToUpperInvariant();

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
    // otherwise still get a job queued for it - the worker can then apply while the transaction itself stays
    // Cancelled. FOR UPDATE makes a concurrent Cancel (which takes the same lock in EfRecordEditor) block
    // until this transaction commits or rolls back, so the status this reads is never stale by the time the
    // insert happens.
    public async Task<bool> EnqueueCategorizationAsync(Guid transactionId, int sourceMessageId, CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        if (await LockAsync(transactionId, cancellationToken) is not { Status: TransactionStatus.Captured } transaction
            || !await IsAwaitingConfirmationAsync(transactionId, cancellationToken))
            return false;

        var now = timeProvider.GetUtcNow();
        if (await IsExchangeSlipAsync(transactionId, cancellationToken))
        {
            // A-8: a reply records a held slip. Queued behind a reply still in flight, the caption would re-apply its
            // own figures over the operator's.
            if (await HasReplyInFlightAsync(transactionId, cancellationToken))
                return false;

            // A held slip is recorded by RecordExchange, its caption right after it (spec §3, Recording 1).
            db.CategorizationJobs.Add(PendingJob(transactionId, JobKind.RecordExchange, now, sourceMessageId));
            if (CaptionJob(transactionId, transaction, now) is { } caption)
                db.CategorizationJobs.Add(caption);
        }
        else
        {
            db.CategorizationJobs.Add(PendingJob(transactionId, JobKind.CategorizeReceipt, now, sourceMessageId));
        }

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
        var receipt = await db.Receipts.AsNoTracking()
            .Where(r => r.TransactionId == transactionId)
            .Select(r => new { r.Source, r.Kind })
            .SingleOrDefaultAsync(cancellationToken);

        if (receipt is not { Source: ReceiptSource.Vision })
            return false;

        return receipt.Kind == ReceiptKind.Exchange
            ? await IsHeldSlipAsync(transactionId, cancellationToken)
            : !await HasJobAsync(transactionId, JobKind.CategorizeReceipt, cancellationToken);
    }

    // Amendment 8: a held slip only - never recorded by anything (no RecordExchange job, no Initial, Correction
    // or Edit revision) and not failed. Status-independent like the fiscal rule, so a Cancelled held slip still
    // offers Restore back to its prompt; an incomplete slip (failed) and one a reply completed (a Correction
    // revision) never offer "Record anyway". The stored evidence must itself assess as Hold: a photo cancelled
    // before its incomplete slip was saved is never marked failed, so only the slip can say it is incomplete.
    async Task<bool> IsHeldSlipAsync(Guid transactionId, CancellationToken cancellationToken) =>
        !await HasJobAsync(transactionId, JobKind.RecordExchange, cancellationToken)
        && !await db.TransactionRevisions.AsNoTracking().AnyAsync(
            revision => revision.TransactionId == transactionId
                && (revision.Kind == RevisionKind.Initial || revision.Kind == RevisionKind.Correction || revision.Kind == RevisionKind.Edit),
            cancellationToken)
        && await db.Transactions.AsNoTracking().AnyAsync(t => t.Id == transactionId && t.FailureReason == RecordFailureReason.None, cancellationToken)
        && await GetExchangeSlipAsync(transactionId, cancellationToken) is { } slip
        && slip.Evidence.Assess(slip.SellerTaxId, taxIdMalformed: false).Disposition == AppReceipts.SlipDisposition.Hold;

    Task<bool> HasJobAsync(Guid transactionId, JobKind kind, CancellationToken cancellationToken) =>
        db.CategorizationJobs.AsNoTracking().AnyAsync(job => job.TransactionId == transactionId && job.Kind == kind, cancellationToken);

    // Until Record anyway, only a reply's job on a slip carries a source message; a caption's never does.
    Task<bool> HasReplyInFlightAsync(Guid transactionId, CancellationToken cancellationToken) =>
        db.CategorizationJobs.AsNoTracking().AnyAsync(
            job => job.TransactionId == transactionId
                && job.SourceMessageId != null
                && (job.Status == JobStatus.Pending || job.Status == JobStatus.Claimed),
            cancellationToken);

    Task<bool> IsExchangeSlipAsync(Guid transactionId, CancellationToken cancellationToken) =>
        db.Receipts.AsNoTracking().AnyAsync(r => r.TransactionId == transactionId && r.Kind == ReceiptKind.Exchange, cancellationToken);

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
