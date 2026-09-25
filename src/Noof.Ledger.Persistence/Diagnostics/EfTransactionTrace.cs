using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Diagnostics;

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

        return new TransactionTrace(transactionId, summary is not null, summary, events, historyViews);
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
