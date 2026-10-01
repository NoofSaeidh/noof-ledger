using Microsoft.EntityFrameworkCore;
using Npgsql;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Balances;
using Noof.Ledger.Persistence.Revisions;

namespace Noof.Ledger.Persistence.Categorization;

internal sealed class EfCategorizationStore(LedgerDbContext db, TimeProvider timeProvider) : ICategorizationStore
{
    public async Task<CategorizationSubject?> GetSubjectAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var header = await (
            from t in db.Transactions.AsNoTracking()
            where t.Id == transactionId
            join w in db.Wallets.AsNoTracking() on t.WalletId equals (Guid?)w.Id into walletJoin
            from w in walletJoin.DefaultIfEmpty()
            select new
            {
                t.Id,
                t.RawText,
                t.TelegramChatId,
                t.BotMessageId,
                WalletName = w == null ? string.Empty : w.Name,
                WalletCurrency = w == null ? (CurrencyCode?)null : w.Currency,
                t.Status,
                t.OccurredAt,
                t.TimeZoneId,
                t.OccurredOn,
                t.CaptureKind,
                t.Kind,
                t.WalletId,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (header is null)
            return null;

        var lines = await (
            from li in db.LineItems.AsNoTracking()
            where li.TransactionId == transactionId
            join c in db.Categories.AsNoTracking() on li.CategoryId equals c.Id into categoryJoin
            from c in categoryJoin.DefaultIfEmpty()
            join m in db.Merchants.AsNoTracking() on li.MerchantId equals m.Id into merchantJoin
            from m in merchantJoin.DefaultIfEmpty()
            orderby li.Ordinal
            select new RecordedLine(
                li.Description,
                li.Amount,
                c == null ? null : c.Slug,
                c == null ? null : c.NameEn,
                m == null ? null : m.DisplayName,
                EntryRole.Principal))
            .ToListAsync(cancellationToken);

        var balances = header.WalletId is { } walletId
            ? await new EfBalanceReadModel(db).BalanceOfAsync(walletId, cancellationToken)
            : [];

        var statement = header.Kind == TransactionKind.BalanceCheck
            ? await db.BalanceChecks.AsNoTracking()
                .Where(bc => bc.TransactionId == transactionId)
                .Select(bc => new BalanceStatement(bc.Stated, bc.ComputedBefore))
                .SingleOrDefaultAsync(cancellationToken)
            : null;

        // A voice capture has no text until its transcript arrives, and none at all when nothing was heard;
        // the pipeline and the echo read that as empty, which is what it is. Only a Manual record has no chat,
        // and nothing categorises or echoes one, so 0 stands in for it.
        return new CategorizationSubject(
            header.Id, header.RawText ?? string.Empty, header.TelegramChatId ?? 0, header.BotMessageId, header.WalletName,
            header.Status, ZonedClock.LocalDate(header.OccurredAt, header.TimeZoneId), header.OccurredOn, lines,
            header.CaptureKind, header.Kind, header.WalletCurrency, balances, statement, WalletId: header.WalletId);
    }

    public async Task ApplyAsync(Guid transactionId, CategorizationOutcome outcome, CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        // Row lock, first statement inside the transaction. PostgreSQL runs READ COMMITTED, so
        // without this a second concurrent ApplyAsync for the same transaction (a worker whose
        // lease expired mid-job plus a second host process, say) would see nothing to DELETE
        // (neither caller has committed yet), both INSERTs would succeed, and both would COMMIT -
        // doubling the bill. FOR UPDATE makes the second caller block here until the first commits
        // and releases the lock, so it then sees (and replaces) the first caller's rows instead of
        // adding to them.
        await db.Database.SqlQueryRaw<Guid>(
            "SELECT id FROM transactions WHERE id = @transactionId FOR UPDATE",
            new NpgsqlParameter("transactionId", transactionId))
            .ToListAsync(cancellationToken);

        // The precedence predicate lives in the DELETE statement itself, not in an `if` around it -
        // a Rule- or User-authored line (2 or 4) is never eligible for deletion by a model re-run,
        // and there is nothing downstream that could forget to check that, because there is nothing
        // to forget.
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM line_items WHERE transaction_id = @transactionId AND categorized_by <= @modelAuthority",
            [
                new NpgsqlParameter("transactionId", transactionId),
                new NpgsqlParameter("modelAuthority", (int)CategorizationAuthority.Model),
            ],
            cancellationToken);

        // Starts after whatever survived the delete above (a Rule- or User-authored line), so a
        // model re-run's fresh ordinals never collide with an ordinal a kept line already owns.
        var ordinal = 1 + (await db.LineItems.AsNoTracking()
            .Where(li => li.TransactionId == transactionId)
            .Select(li => (int?)li.Ordinal)
            .MaxAsync(cancellationToken) ?? 0);
        foreach (var item in outcome.Items)
        {
            // A receipt line names its own Ordinal - the receipt's own order (R-2) - and never the
            // auto-numbering below, which exists only for a text/voice capture's model-authored lines.
            var itemOrdinal = item.Ordinal ?? ordinal;
            db.LineItems.Add(new LineItem
            {
                Id = Guid.NewGuid(),
                TransactionId = transactionId,
                Description = item.Description,
                Amount = item.Amount,
                CategoryId = item.CategoryId,
                CategorizedBy = CategorizationAuthority.Model,
                MerchantId = item.MerchantId,
                Ordinal = itemOrdinal,
                ReceiptLineId = item.ReceiptLineId,
            });

            if (item.Ordinal is null)
                ordinal++;
        }

        var transaction = await db.Transactions.SingleAsync(t => t.Id == transactionId, cancellationToken);
        var statusBefore = transaction.Status;
        // A correction arriving for a cancelled record corrects it and leaves it cancelled; only Restore
        // brings it back.
        transaction.Status = statusBefore == TransactionStatus.Cancelled ? TransactionStatus.Cancelled : TransactionStatus.Completed;
        transaction.OccurredOn = outcome.OccurredOn;
        transaction.Kind = outcome.TransactionKind;
        transaction.WalletId = outcome.WalletId ?? transaction.WalletId;

        await db.SaveChangesAsync(cancellationToken);
        await LedgerPostings.RewriteAsync(db, transaction, outcome.StatedBalance, cancellationToken);
        await RevisionLog.AppendAsync(db, transaction, RevisionKindFor(outcome), outcome.Instruction,
            statusBefore, timeProvider.GetUtcNow(), cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    // CategorizeReceipt runs twice for the same receipt (I-2, Phase 6 final review): once from
    // ExtractReceiptWorker's own hand-off (no Instruction - the first, Initial reading) and again from
    // a receipt correction routed here instead of record_transaction (always carries the operator's
    // Instruction) - the two are told apart the same way Correct already is, by whether one is present.
    static RevisionKind RevisionKindFor(CategorizationOutcome outcome) => outcome.Kind switch
    {
        JobKind.Categorize => RevisionKind.Initial,
        JobKind.CategorizeReceipt => outcome.Instruction is null ? RevisionKind.Initial : RevisionKind.Correction,
        JobKind.Correct => RevisionKind.Correction,
        JobKind.Reinterpret => RevisionKind.Edit,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome.Kind, "No revision kind for this job kind."),
    };

    public Task MarkFailedAsync(Guid transactionId, RecordFailureReason reason, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(
            "UPDATE transactions SET status = @status, failure_reason = @reason WHERE id = @transactionId AND status = @captured",
            [
                new NpgsqlParameter("status", (int)TransactionStatus.Failed),
                new NpgsqlParameter("reason", (int)reason),
                new NpgsqlParameter("transactionId", transactionId),
                new NpgsqlParameter("captured", (object)(int)TransactionStatus.Captured),
            ],
            cancellationToken);
}
