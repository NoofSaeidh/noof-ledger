using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Reporting.Summary;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Reporting;

internal sealed class EfSummaryRowsReader(LedgerDbContext db) : ISummaryRowsReader
{
    // Every wallet, archived and empty ones included, with the first day anything counted on it over the whole history
    // (A-5): a wallet with nothing in the months read still starts the history of all wallets.
    const string WalletsSql = SummaryLines.CountedCte + "\n" + """
        SELECT w.id AS "Id",
               w.name AS "Name",
               w.currency AS "Currency",
               w.archived AS "Archived",
               first_counted.occurred_on AS "FirstCountedOn"
        FROM wallets w
        LEFT JOIN (SELECT cl.wallet_id, MIN(cl.occurred_on) AS occurred_on FROM counted cl GROUP BY cl.wallet_id) first_counted
            ON first_counted.wallet_id = w.id
        ORDER BY w.name, w.id
        """;

    // From and To as stored, the fee inside its leg (T-12); the fee itself is the record's Fee line.
    const string TransfersSql = """
        SELECT t.id AS "TransactionId",
               t.occurred_on AS "OccurredOn",
               tr.from_wallet_id AS "FromWalletId",
               tr.from_amount AS "FromAmount",
               tr.from_currency AS "FromCurrency",
               tr.to_wallet_id AS "ToWalletId",
               tr.to_amount AS "ToAmount",
               tr.to_currency AS "ToCurrency",
               tr.fee_leg AS "FeeLeg",
               (SELECT SUM(f.amount) FROM line_items f WHERE f.transaction_id = t.id AND f.role = @fee) AS "Fee",
               venue.display_name AS "VenueName"
        FROM transfers tr
        JOIN transactions t ON t.id = tr.transaction_id
        LEFT JOIN merchants venue ON venue.id = tr.venue_merchant_id
        WHERE t.kind = @transfer
          AND t.status <> @cancelled
          AND t.occurred_on >= @from
          AND t.occurred_on < @toExclusive
        ORDER BY t.occurred_on, t.id
        """;

    public async Task<SummaryRows> ReadAsync(DateOnly from, DateOnly toExclusive, CancellationToken cancellationToken)
    {
        var wallets = await db.Database.SqlQueryRaw<SummaryWalletSqlRow>(WalletsSql, [.. SummaryLines.CountedParameters()])
            .ToListAsync(cancellationToken);
        var lines = await SummaryLines.ReadAsync(db, from, toExclusive, cancellationToken);
        var transfers = await db.Database.SqlQueryRaw<SummaryTransferSqlRow>(
                TransfersSql,
                [
                    SummaryLines.Integer("fee", (int)EntryRole.Fee),
                    SummaryLines.Integer("transfer", (int)TransactionKind.Transfer),
                    SummaryLines.Integer("cancelled", (int)TransactionStatus.Cancelled),
                    .. SummaryLines.Range(from, toExclusive),
                ])
            .ToListAsync(cancellationToken);

        return new SummaryRows(
            [.. wallets.Select(ToWallet)],
            [.. lines.Select(ToLine).OfType<SummaryLineRow>()],
            [.. transfers.Select(ToTransfer)]);
    }

    public Task<bool> HasUnsettledWorkAsync(DateOnly since, CancellationToken cancellationToken) =>
        db.CategorizationJobs.AsNoTracking().AnyAsync(
            job => (job.Status == JobStatus.Pending || job.Status == JobStatus.Claimed)
                && db.Transactions.Any(transaction => transaction.Id == job.TransactionId
                    && transaction.Status != TransactionStatus.Cancelled
                    && transaction.OccurredOn >= since),
            cancellationToken);

    static SummaryWallet ToWallet(SummaryWalletSqlRow row) =>
        new(row.Id, row.Name, new CurrencyCode(row.Currency), row.Archived, MonthOf(row.FirstCountedOn));

    // A line on a record with no wallet has no wallet currency to count in, so the summary leaves it out; Home's This
    // month, which never converts, still counts it in its own currency.
    static SummaryLineRow? ToLine(SummaryLineSqlRow row) => row is { WalletId: { } walletId, WalletCurrency: { } walletCurrency }
        ? new SummaryLineRow(
            row.TransactionId, row.OccurredOn, walletId, new CurrencyCode(walletCurrency), (SummaryLineKind)row.Kind,
            (EntryRole)row.Role, row.Ordinal, row.CategoryName, row.MerchantName, row.Description, row.Amount,
            new CurrencyCode(row.Currency), row.ChargedAmount)
        : null;

    static SummaryTransferRow ToTransfer(SummaryTransferSqlRow row) => new(
        row.TransactionId,
        row.OccurredOn,
        row.FromWalletId,
        new Money(row.FromAmount, new CurrencyCode(row.FromCurrency)),
        row.ToWalletId,
        new Money(row.ToAmount, new CurrencyCode(row.ToCurrency)),
        (TransferLeg?)row.FeeLeg,
        row.Fee,
        row.VenueName);

    static DateOnly? MonthOf(DateOnly? day) => day is { } first ? new DateOnly(first.Year, first.Month, 1) : null;
}

internal sealed class SummaryWalletSqlRow
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;
    public bool Archived { get; init; }
    public DateOnly? FirstCountedOn { get; init; }
}

internal sealed class SummaryTransferSqlRow
{
    public Guid TransactionId { get; init; }
    public DateOnly OccurredOn { get; init; }
    public Guid FromWalletId { get; init; }
    public decimal FromAmount { get; init; }
    public string FromCurrency { get; init; } = string.Empty;
    public Guid ToWalletId { get; init; }
    public decimal ToAmount { get; init; }
    public string ToCurrency { get; init; } = string.Empty;
    public int? FeeLeg { get; init; }
    public decimal? Fee { get; init; }
    public string? VenueName { get; init; }
}
