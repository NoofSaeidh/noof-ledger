using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Persistence.Diagnostics;

internal sealed class EfTransactionTrace(LedgerDbContext db) : ITransactionTrace
{
    public async Task<TransactionTrace> GetAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var exists = await db.Transactions.AsNoTracking()
            .AnyAsync(t => t.Id == transactionId, cancellationToken);

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

        return new TransactionTrace(transactionId, exists, events, historyViews);
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

        return new TraceEvent(entry.LoggedAt, stage, eventId, entry.Level, entry.Message, entry.Exception, entry.PropertiesJson, failedStage);
    }
}
