using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Reporting;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Reporting;

internal sealed class EfTransactionList(LedgerDbContext db) : ITransactionList
{
    public async Task<TransactionListPage> QueryAsync(
        TransactionListFilter filter, int pageIndex, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.Transactions.AsNoTracking().AsQueryable();

        if (filter.From is { } from)
            query = query.Where(t => t.OccurredOn >= from);
        if (filter.To is { } to)
            query = query.Where(t => t.OccurredOn <= to);
        if (filter.WalletId is { } walletId)
            query = query.Where(t => t.WalletId == walletId);
        if (filter.Kind is { } kind)
            query = query.Where(t => t.Kind == kind);
        if (filter.Status is { } status)
            query = query.Where(t => t.Status == status);
        // M-3 (Phase 5 final review): Blazor binds a cleared <input> to "", not null - an empty
        // filter must behave like no filter, not like `RawText != null`, which would hide every
        // voice capture awaiting transcription (RawText is null until ReplaceRawTextAsync runs).
        if (filter.Text is { } text && !string.IsNullOrWhiteSpace(text))
        {
            var pattern = $"%{EscapeLike(text)}%";
            query = query.Where(t => t.RawText != null && EF.Functions.ILike(t.RawText, pattern, @"\"));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var headers = await (
                from t in query
                join w in db.Wallets.AsNoTracking() on t.WalletId equals (Guid?)w.Id into walletJoin
                from w in walletJoin.DefaultIfEmpty()
                select new
                {
                    t.Id,
                    t.OccurredOn,
                    t.OccurredAt,
                    t.TimeZoneId,
                    t.Kind,
                    t.Status,
                    t.RawText,
                    WalletName = w == null ? string.Empty : w.Name,
                })
            .OrderByDescending(h => h.OccurredOn)
            .ThenByDescending(h => h.OccurredAt)
            .ThenByDescending(h => h.Id)
            .Skip(pageIndex * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var transactionIds = headers.Select(h => h.Id).ToList();

        var lineItemRows = await (
            from li in db.LineItems.AsNoTracking()
            where transactionIds.Contains(li.TransactionId)
            join c in db.Categories.AsNoTracking() on li.CategoryId equals c.Id into categoryJoin
            from c in categoryJoin.DefaultIfEmpty()
            select new { li.TransactionId, li.Amount, CategoryName = c == null ? null : c.NameEn })
            .ToListAsync(cancellationToken);
        var lineItemsByTransaction = lineItemRows.ToLookup(r => r.TransactionId);

        var entryRows = await db.Entries.AsNoTracking()
            .Where(e => transactionIds.Contains(e.TransactionId))
            .Select(e => new { e.TransactionId, e.Amount })
            .ToListAsync(cancellationToken);
        var entriesByTransaction = entryRows.ToLookup(r => r.TransactionId);

        var balanceChecksByTransaction = await db.BalanceChecks.AsNoTracking()
            .Where(bc => transactionIds.Contains(bc.TransactionId))
            .ToDictionaryAsync(bc => bc.TransactionId, bc => bc.Stated, cancellationToken);

        IReadOnlyList<Money> AmountsFor(Guid transactionId, TransactionKind kind) => kind switch
        {
            TransactionKind.Expense =>
            [
                .. lineItemsByTransaction[transactionId]
                    .GroupBy(r => r.Amount.Currency)
                    .Select(g => new Money(g.Sum(r => r.Amount.Amount), g.Key)),
            ],
            TransactionKind.Income =>
            [
                .. entriesByTransaction[transactionId]
                    .GroupBy(r => r.Amount.Currency)
                    .Select(g => new Money(g.Sum(r => r.Amount.Amount), g.Key)),
            ],
            TransactionKind.BalanceCheck => balanceChecksByTransaction.TryGetValue(transactionId, out var stated) ? [stated] : [],
            _ => [],
        };

        IReadOnlyList<string> CategoriesFor(Guid transactionId) =>
        [
            .. lineItemsByTransaction[transactionId]
                .Select(r => r.CategoryName)
                .Where(name => name is not null)
                .Select(name => name!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal),
        ];

        var rows = headers
            .Select(h => new TransactionListRow(
                h.Id,
                h.OccurredOn,
                LocalTimeOf(h.OccurredAt, h.TimeZoneId, h.OccurredOn),
                h.WalletName,
                h.Kind,
                h.Status,
                h.RawText,
                AmountsFor(h.Id, h.Kind),
                CategoriesFor(h.Id)))
            .ToList();

        return new TransactionListPage(rows, totalCount);
    }

    static TimeOnly? LocalTimeOf(DateTimeOffset occurredAt, string timeZoneId, DateOnly occurredOn)
    {
        var local = ZonedClock.LocalDateTime(occurredAt, timeZoneId);
        return DateOnly.FromDateTime(local) == occurredOn ? TimeOnly.FromDateTime(local) : null;
    }

    static string EscapeLike(string text) =>
        text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}
