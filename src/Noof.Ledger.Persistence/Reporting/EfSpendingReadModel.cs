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

    // Spending is the Principal lines of expenses plus every Fee line, a transfer's included (spec §1, §6); income is
    // the Principal lines of incomes. Roles are filtered explicitly, never trusted from the kind (spec §2).
    const string MonthTotalsSql = """
        SELECT t.kind = @income AS received,
               COALESCE(c.name_en, @uncategorised) AS category_name,
               li.currency AS currency,
               SUM(li.amount) AS total
        FROM line_items li
        JOIN transactions t ON t.id = li.transaction_id
        LEFT JOIN categories c ON c.id = li.category_id
        WHERE t.occurred_on >= @firstDay
          AND t.occurred_on < @firstDayNextMonth
          AND t.status <> @cancelled
          AND ((li.role = @principal AND t.kind IN (@expense, @income))
            OR (li.role = @fee AND t.kind IN (@expense, @transfer)))
        GROUP BY 1, 2, 3
        """;

    public async Task<IReadOnlyList<RecentTransaction>> RecentAsync(int limit, RecentView view, CancellationToken cancellationToken)
    {
        var transactions = view switch
        {
            RecentView.SpendingAndIncome => db.Transactions.Where(t => t.Kind != TransactionKind.Transfer),
            RecentView.Transfers => db.Transactions.Where(t => t.Kind == TransactionKind.Transfer),
            RecentView.All => db.Transactions.AsQueryable(),
            _ => throw new ArgumentOutOfRangeException(nameof(view), view, "Unknown recent view."),
        };

        var headers = await (
                from t in transactions
                where t.Status != TransactionStatus.Cancelled
                join w in db.Wallets on t.WalletId equals (Guid?)w.Id into walletJoin
                from w in walletJoin.DefaultIfEmpty()
                select new
                {
                    t.Id,
                    t.OccurredOn,
                    t.OccurredAt,
                    t.TimeZoneId,
                    RawText = t.RawText ?? string.Empty,
                    t.Status,
                    t.Kind,
                    WalletName = w == null ? string.Empty : w.Name,
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
                li.Ordinal,
                li.Description,
                li.Amount,
                CategoryName = c == null ? null : c.NameEn,
                MerchantName = m == null ? null : m.DisplayName,
                li.Role,
            })
            .ToListAsync(cancellationToken);

        var lineItemsByTransaction = lineItemRows.ToLookup(r => r.TransactionId);

        var transfers = await TransferLines.ForAsync(
            db, [.. headers.Where(h => h.Kind == TransactionKind.Transfer).Select(h => h.Id)], cancellationToken);

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
                    .OrderBy(li => li.Ordinal)
                    .Select(li => new RecentLineItem(li.Description, li.Amount, li.CategoryName, li.MerchantName, li.Role))],
                h.Kind,
                transfers.GetValueOrDefault(h.Id))),
        ];
    }

    static TimeOnly? LocalTimeOf(DateTimeOffset occurredAt, string timeZoneId, DateOnly occurredOn)
    {
        var local = ZonedClock.LocalDateTime(occurredAt, timeZoneId);
        return DateOnly.FromDateTime(local) == occurredOn ? TimeOnly.FromDateTime(local) : null;
    }

    public async Task<MonthSummary> ThisMonthAsync(CancellationToken cancellationToken)
    {
        var (firstDay, firstDayNextMonth) = ThisMonth();

        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();

            await using var command = connection.CreateCommand();
            command.Transaction = (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = MonthTotalsSql;
            command.Parameters.Add(new NpgsqlParameter("uncategorised", UncategorisedLabel));
            command.Parameters.Add(new NpgsqlParameter("firstDay", firstDay));
            command.Parameters.Add(new NpgsqlParameter("firstDayNextMonth", firstDayNextMonth));
            command.Parameters.Add(Integer("cancelled", (int)TransactionStatus.Cancelled));
            command.Parameters.Add(Integer("expense", (int)TransactionKind.Expense));
            command.Parameters.Add(Integer("income", (int)TransactionKind.Income));
            command.Parameters.Add(Integer("transfer", (int)TransactionKind.Transfer));
            command.Parameters.Add(Integer("principal", (int)EntryRole.Principal));
            command.Parameters.Add(Integer("fee", (int)EntryRole.Fee));

            var spent = new List<MonthTotal>();
            var received = new List<MonthTotal>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var total = new MonthTotal(reader.GetString(1), new CurrencyCode(reader.GetString(2)), reader.GetDecimal(3));
                    (reader.GetBoolean(0) ? received : spent).Add(total);
                }
            }

            return new MonthSummary(firstDay, spent, received);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public async Task<IReadOnlyList<MonthTransfer>> TransfersThisMonthAsync(CancellationToken cancellationToken)
    {
        var (firstDay, firstDayNextMonth) = ThisMonth();

        var headers = await db.Transactions.AsNoTracking()
            .Where(t => t.Kind == TransactionKind.Transfer
                && t.Status != TransactionStatus.Cancelled
                && t.OccurredOn >= firstDay
                && t.OccurredOn < firstDayNextMonth)
            .OrderByDescending(t => t.OccurredOn)
            .ThenByDescending(t => t.OccurredAt)
            .ThenByDescending(t => t.Id)
            .Select(t => new { t.Id, t.OccurredOn })
            .ToListAsync(cancellationToken);

        var lines = await TransferLines.ForAsync(db, [.. headers.Select(h => h.Id)], cancellationToken);

        return [.. headers.Where(h => lines.ContainsKey(h.Id)).Select(h => new MonthTransfer(h.Id, h.OccurredOn, lines[h.Id]))];
    }

    (DateOnly FirstDay, DateOnly FirstDayNextMonth) ThisMonth()
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), currentZone).DateTime);
        var firstDay = new DateOnly(today.Year, today.Month, 1);
        return (firstDay, firstDay.AddMonths(1));
    }

    // A constant 0 converts implicitly to any enum, so new NpgsqlParameter("x", 0) - TransactionKind.Expense,
    // EntryRole.Principal - binds to the NpgsqlDbType/DbType overload instead of carrying the value. An int parameter is
    // not a constant, so this always reaches (string, object).
    static NpgsqlParameter Integer(string name, int value) => new(name, value);
}
