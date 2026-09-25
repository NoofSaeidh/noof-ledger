using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Persistence.Diagnostics;

internal sealed class EfLogRetention(LedgerDbContext db) : ILogRetention
{
    public async Task<int> PruneAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var deleted = await db.AppLogs
            .Where(e => e.Level <= LogSeverity.Debug && e.LoggedAt < now.AddDays(-7))
            .ExecuteDeleteAsync(cancellationToken);

        deleted += await db.AppLogs
            .Where(e => e.Level == LogSeverity.Information && e.LoggedAt < now.AddDays(-90))
            .ExecuteDeleteAsync(cancellationToken);

        deleted += await db.AppLogs
            .Where(e => e.Level >= LogSeverity.Warning && e.LoggedAt < now.AddDays(-730))
            .ExecuteDeleteAsync(cancellationToken);

        return deleted;
    }
}
