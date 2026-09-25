using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Persistence.Diagnostics;

internal sealed class EfLogRetention(LedgerDbContext db, TimeProvider timeProvider, LogRetentionOptions options) : ILogRetention
{
    public async Task<int> PruneAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var deleted = 0;

        foreach (var level in Enum.GetValues<LogSeverity>())
        {
            var cutoff = now.AddDays(-options.Days.For(level));
            deleted += await db.AppLogs
                .Where(e => e.Level == level && e.LoggedAt < cutoff)
                .ExecuteDeleteAsync(cancellationToken);
        }

        return deleted;
    }
}
