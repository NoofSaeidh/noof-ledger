using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Noof.Ledger.Application.Reporting;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Reporting;

internal sealed class EfSpendingReadModel(LedgerDbContext db, TimeProvider timeProvider, TimeZoneInfo currentZone)
    : ISpendingReadModel
{
    internal const string UncategorisedLabel = "Uncategorised";

    public async Task<IReadOnlyList<RecentTransaction>> RecentAsync(int limit, CancellationToken cancellationToken)
    {
        var headers = await db.Transactions
            .Where(t => t.Status != TransactionStatus.Cancelled)
            .Join(db.Wallets, t => t.WalletId, w => w.Id, (t, w) => new
            {
                t.Id,
                t.OccurredOn,
                t.OccurredAt,
                t.TimeZoneId,
                t.RawText,
                t.Status,
                WalletName = w.Name,
            })
            .OrderByDescending(h => h.OccurredOn)
            .ThenByDescending(h => h.OccurredAt)
            .ThenByDescending(h => h.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var transactionIds = headers.Select(h => h.Id).ToList();

        // li is never the optional side of a join here - only categories/merchants can be absent -
        // so this stays one flat SELECT with two LEFT JOINs, run once for every transaction header
        // fetched above rather than once per transaction. See Decision 1 for why the two entire
        // queries are not merged into one.
        var lineItemRows = await (
            from li in db.LineItems
            where transactionIds.Contains(li.TransactionId)
            join c in db.Categories on li.CategoryId equals c.Id into categoryJoin
            from c in categoryJoin.DefaultIfEmpty()
            join m in db.Merchants on li.MerchantId equals m.Id into merchantJoin
            from m in merchantJoin.DefaultIfEmpty()
            select new
            {
                li.TransactionId,
                li.Description,
                li.Amount,
                CategoryName = c == null ? null : c.NameEn,
                MerchantName = m == null ? null : m.DisplayName,
            })
            .ToListAsync(cancellationToken);

        var lineItemsByTransaction = lineItemRows.ToLookup(r => r.TransactionId);

        return
        [
            .. headers.Select(h => new RecentTransaction(
                h.Id,
                h.OccurredOn,
                LocalTimeOf(h.OccurredAt, h.TimeZoneId, h.OccurredOn),
                h.RawText,
                h.Status,
                h.WalletName,
                [.. lineItemsByTransaction[h.Id]
                    .Select(li => new RecentLineItem(li.Description, li.Amount, li.CategoryName, li.MerchantName))])),
        ];
    }

    static TimeOnly? LocalTimeOf(DateTimeOffset occurredAt, string timeZoneId, DateOnly occurredOn)
    {
        var local = ZonedClock.LocalDateTime(occurredAt, timeZoneId);
        return DateOnly.FromDateTime(local) == occurredOn ? TimeOnly.FromDateTime(local) : null;
    }

    public async Task<MonthSummary> ThisMonthAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), currentZone).DateTime);
        var firstDay = new DateOnly(today.Year, today.Month, 1);
        var firstDayNextMonth = firstDay.AddMonths(1);

        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();

            await using var command = connection.CreateCommand();
            command.Transaction = (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText =
                """
                SELECT COALESCE(c.name_en, @uncategorised) AS category_name,
                       li.currency AS currency,
                       SUM(li.amount) AS total
                FROM line_items li
                JOIN transactions t ON t.id = li.transaction_id
                LEFT JOIN categories c ON c.id = li.category_id
                WHERE t.occurred_on >= @firstDay
                  AND t.occurred_on < @firstDayNextMonth
                  AND t.status <> @cancelled
                GROUP BY COALESCE(c.name_en, @uncategorised), li.currency
                """;
            command.Parameters.Add(new NpgsqlParameter("uncategorised", UncategorisedLabel));
            command.Parameters.Add(new NpgsqlParameter("firstDay", firstDay));
            command.Parameters.Add(new NpgsqlParameter("firstDayNextMonth", firstDayNextMonth));
            command.Parameters.Add(new NpgsqlParameter("cancelled", (int)TransactionStatus.Cancelled));

            var totals = new List<MonthTotal>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    totals.Add(new MonthTotal(
                        reader.GetString(0),
                        new CurrencyCode(reader.GetString(1)),
                        reader.GetDecimal(2)));
                }
            }

            return new MonthSummary(firstDay, totals);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
