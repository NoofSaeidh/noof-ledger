using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Reporting;
using Noof.Ledger.Application.Reporting.Summary;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Reporting;

internal sealed class EfSpendingReadModel(LedgerDbContext db, TimeProvider timeProvider, TimeZoneInfo currentZone)
    : ISpendingReadModel
{
    internal const string UncategorisedLabel = "Uncategorised";

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
        var counted = Counted(await SummaryLines.ReadAsync(db, firstDay, firstDayNextMonth, cancellationToken));

        return new MonthSummary(
            firstDay,
            TotalsOf(counted.Where(line => line.Kind is SummaryLineKind.Spent or SummaryLineKind.Refund)),
            TotalsOf(counted.Where(line => line.Kind is SummaryLineKind.Received)));
    }

    // A foreign line a charge prices counts as its share of the charge, in the wallet's currency (8b spec A-1), split by
    // Money.Allocate in ordinal order exactly as the summary splits it; every other line counts as it is, in its own
    // currency - This month never converts.
    static IReadOnlyList<CountedLine> Counted(IReadOnlyList<SummaryLineSqlRow> lines) =>
    [
        .. lines
            .Where(line => line.ChargedAmount is null)
            .Select(line => new CountedLine(
                (SummaryLineKind)line.Kind, line.CategoryName, new Money(line.Amount, new CurrencyCode(line.Currency)))),
        .. lines
            .Where(line => line.ChargedAmount is not null)
            .GroupBy(line => (line.TransactionId, line.Currency))
            .SelectMany(ShareOfCharge),
    ];

    static IEnumerable<CountedLine> ShareOfCharge(IEnumerable<SummaryLineSqlRow> linesOfOneCharge)
    {
        var lines = linesOfOneCharge.OrderBy(line => line.Ordinal).ToList();
        // Grouped from lines that carry a charge, and a charge exists only on an expense with a wallet (ForeignCharges).
        var charge = new Money(lines[0].ChargedAmount!.Value, new CurrencyCode(lines[0].WalletCurrency!));
        var shares = charge.Allocate([.. lines.Select(line => line.Amount)]);
        return lines.Zip(shares, (line, share) => new CountedLine((SummaryLineKind)line.Kind, line.CategoryName, share));
    }

    // A refund counts against its category (8b spec SS-19), so a category whose refunds exceed its purchases goes below
    // zero.
    static IReadOnlyList<MonthTotal> TotalsOf(IEnumerable<CountedLine> lines) =>
    [
        .. lines
            .GroupBy(line => (line.CategoryName, line.Amount.Currency))
            .Select(group => new MonthTotal(
                group.Key.CategoryName,
                group.Key.Currency,
                group.Sum(line => line.Kind is SummaryLineKind.Refund ? -line.Amount.Amount : line.Amount.Amount))),
    ];

    readonly record struct CountedLine(SummaryLineKind Kind, string CategoryName, Money Amount);

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
}
