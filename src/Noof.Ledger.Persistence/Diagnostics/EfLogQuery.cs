using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Persistence.Diagnostics;

internal sealed class EfLogQuery(LedgerDbContext db) : ILogQuery
{
    public async Task<LogPage> QueryAsync(LogFilter filter, int pageIndex, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.AppLogs.AsNoTracking().Where(e => e.Level >= filter.MinLevel);

        if (filter.From is { } from)
            query = query.Where(e => e.LoggedAt >= from);
        if (filter.To is { } to)
            query = query.Where(e => e.LoggedAt <= to);
        if (filter.Source is { } source)
        {
            var sourcePattern = $"{EscapeLike(source)}%";
            query = query.Where(e => e.Source != null && EF.Functions.ILike(e.Source, sourcePattern, @"\"));
        }
        if (filter.TransactionId is { } transactionId)
            query = query.Where(e => e.TransactionId == transactionId);
        if (filter.Text is { } text)
        {
            var pattern = $"%{EscapeLike(text)}%";
            query = query.Where(e => EF.Functions.ILike(e.Message, pattern, @"\"));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var sorted = filter.Sort == LogSortOrder.OldestFirst
            ? query.OrderBy(e => e.LoggedAt).ThenBy(e => e.Id)
            : query.OrderByDescending(e => e.LoggedAt).ThenByDescending(e => e.Id);

        var rows = await sorted
            .Skip(pageIndex * pageSize)
            .Take(pageSize)
            .Select(e => new LogRow(e.Id, e.LoggedAt, e.Level, e.Source, e.Message, e.Exception, e.TransactionId, e.PropertiesJson))
            .ToListAsync(cancellationToken);

        return new LogPage(rows, totalCount);
    }

    // Escapes the three characters ILIKE treats specially so a literal "%" or "_" the operator typed is
    // matched literally, not as a wildcard — the contract's "ILIKE on message with escaped wildcards".
    static string EscapeLike(string text) =>
        text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}
