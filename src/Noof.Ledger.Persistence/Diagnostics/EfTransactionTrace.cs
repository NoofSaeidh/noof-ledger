using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Diagnostics;
using AppReceipts = Noof.Ledger.Application.Receipts;

namespace Noof.Ledger.Persistence.Diagnostics;

internal sealed class EfTransactionTrace(LedgerDbContext db) : ITransactionTrace
{
    public async Task<TransactionTrace> GetAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var summary = await LoadSummaryAsync(transactionId, cancellationToken);

        var rows = await db.AppLogs.AsNoTracking()
            .Where(e => e.TransactionId == transactionId && e.PropertiesJson != null)
            .OrderBy(e => e.LoggedAt).ThenBy(e => e.Id)
            .ToListAsync(cancellationToken);

        var events = rows
            .Select(ToTraceEvent)
            .Where(e => e is not null)
            .Select(e => e!)
            .ToList();

        var history = await db.TransactionRevisions.AsNoTracking()
            .Where(r => r.TransactionId == transactionId)
            .OrderBy(r => r.RevisionNumber)
            .ToListAsync(cancellationToken);

        var historyViews = history
            .Select(r => new RevisionView(
                r.CreatedAt,
                r.Kind.ToString(),
                r.Instruction ?? $"{r.StatusBefore} → {r.StatusAfter}"))
            .ToList();

        var receipt = await ReceiptTraceAsync(transactionId, cancellationToken);

        return new TransactionTrace(transactionId, summary is not null, summary, events, historyViews, receipt);
    }

    async Task<TransactionSummary?> LoadSummaryAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var header = await (
                from t in db.Transactions.AsNoTracking()
                where t.Id == transactionId
                join w in db.Wallets.AsNoTracking() on t.WalletId equals (Guid?)w.Id into walletJoin
                from w in walletJoin.DefaultIfEmpty()
                select new
                {
                    t.RawText,
                    t.CaptureKind,
                    t.CreatedAt,
                    t.Status,
                    t.Kind,
                    t.OccurredOn,
                    WalletName = w == null ? null : w.Name,
                })
            .SingleOrDefaultAsync(cancellationToken);

        if (header is null)
            return null;

        var lineItems = await (
                from li in db.LineItems.AsNoTracking()
                where li.TransactionId == transactionId
                join c in db.Categories.AsNoTracking() on li.CategoryId equals c.Id into categoryJoin
                from c in categoryJoin.DefaultIfEmpty()
                orderby li.Id
                select new TraceLineItem(li.Description, li.Amount, c == null ? null : c.NameEn))
            .ToListAsync(cancellationToken);

        return new TransactionSummary(
            header.RawText,
            header.CaptureKind,
            header.CreatedAt,
            header.Status,
            header.Kind,
            header.WalletName,
            header.OccurredOn,
            lineItems);
    }

    async Task<ReceiptTraceView?> ReceiptTraceAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var receipt = await db.Receipts.AsNoTracking()
            .SingleOrDefaultAsync(r => r.TransactionId == transactionId, cancellationToken);

        if (receipt is null)
            return null;

        var categoryByReceiptLineId = await (
            from lineItem in db.LineItems.AsNoTracking()
            where lineItem.TransactionId == transactionId && lineItem.ReceiptLineId != null
            join category in db.Categories.AsNoTracking() on lineItem.CategoryId equals category.Id into categoryJoin
            from category in categoryJoin.DefaultIfEmpty()
            select new { lineItem.ReceiptLineId, CategoryNameEn = category == null ? null : category.NameEn })
            .ToDictionaryAsync(row => row.ReceiptLineId!.Value, row => row.CategoryNameEn, cancellationToken);

        var receiptLines = await db.ReceiptLines.AsNoTracking()
            .Where(line => line.ReceiptId == receipt.Id)
            .OrderBy(line => line.Ordinal)
            .ToListAsync(cancellationToken);

        var lines = receiptLines
            .Select(line => new ReceiptTraceLine(
                line.Ordinal, line.Name, line.Quantity, line.Unit, line.UnitPrice, line.Total,
                categoryByReceiptLineId.TryGetValue(line.Id, out var categoryNameEn) ? categoryNameEn : null))
            .ToList();

        return new ReceiptTraceView(
            (AppReceipts.ReceiptSource)receipt.Source,
            receipt.SellerName,
            receipt.LocationName,
            receipt.SellerAddress,
            receipt.SellerTaxId,
            receipt.FiscalNumber,
            receipt.IssuedAt,
            (AppReceipts.PaymentMethod?)receipt.PaymentMethod,
            receipt.Total.Amount,
            receipt.Total.Currency,
            receipt.QrTotal,
            lines);
    }

    static TraceEvent? ToTraceEvent(AppLogEntry entry)
    {
        using var properties = JsonDocument.Parse(entry.PropertiesJson!);
        if (!properties.RootElement.TryGetProperty(TransactionStages.StageProperty, out var stageElement)
            || stageElement.GetString() is not { } stage)
            return null;

        var eventId = properties.RootElement.TryGetProperty("EventId", out var eventIdElement)
            && eventIdElement.TryGetProperty("Id", out var idElement)
                ? idElement.GetInt32()
                : 0;

        var failedStage = properties.RootElement.TryGetProperty("FailedStage", out var failedStageElement)
            ? failedStageElement.GetString()
            : null;

        var reason = ExceptionReason.Extract(entry.Exception);

        return new TraceEvent(entry.LoggedAt, stage, eventId, entry.Level, entry.Message, entry.Exception, entry.PropertiesJson, failedStage, reason);
    }
}
