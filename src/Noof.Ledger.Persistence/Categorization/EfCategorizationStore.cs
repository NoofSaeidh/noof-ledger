using Microsoft.EntityFrameworkCore;
using Npgsql;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Categorization;

public sealed class EfCategorizationStore(LedgerDbContext db) : ICategorizationStore
{
    public async Task<CategorizationSubject?> GetSubjectAsync(Guid transactionId, CancellationToken cancellationToken) =>
        await (
            from t in db.Transactions.AsNoTracking()
            join w in db.Wallets.AsNoTracking() on t.WalletId equals w.Id
            where t.Id == transactionId
            select new CategorizationSubject(t.Id, t.RawText, t.TelegramChatId, t.BotMessageId, w.Name))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task ApplyAsync(Guid transactionId, IReadOnlyList<CategorizedLineItem> items, CancellationToken cancellationToken)
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

        foreach (var item in items)
        {
            db.LineItems.Add(new LineItem
            {
                Id = Guid.NewGuid(),
                TransactionId = transactionId,
                Description = item.Description,
                Amount = item.Amount,
                CategoryId = item.CategoryId,
                CategorizedBy = CategorizationAuthority.Model,
                MerchantId = item.MerchantId,
            });
        }

        var transaction = await db.Transactions.SingleAsync(t => t.Id == transactionId, cancellationToken);
        transaction.Status = TransactionStatus.Completed;

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public Task MarkFailedAsync(Guid transactionId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(
            "UPDATE transactions SET status = @status WHERE id = @transactionId",
            [
                new NpgsqlParameter("status", (int)TransactionStatus.Failed),
                new NpgsqlParameter("transactionId", transactionId),
            ],
            cancellationToken);
}
