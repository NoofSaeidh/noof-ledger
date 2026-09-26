using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Persistence.Diagnostics;

internal sealed class EfDatabaseLogLevelStore(LedgerDbContext db, TimeProvider timeProvider) : IDatabaseLogLevelStore
{
    const string Key = "logging.database-minimum-level";

    public async Task<LogSeverity?> GetAsync(CancellationToken cancellationToken)
    {
        var row = await db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == Key, cancellationToken);

        return row is not null && Enum.TryParse<LogSeverity>(row.Value, out var level) && Enum.IsDefined(level)
            ? level
            : null;
    }

    public async Task SaveAsync(LogSeverity level, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO app_setting (key, value, updated_at) VALUES ({Key}, {level.ToString()}, {now})
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = EXCLUDED.updated_at
            """,
            cancellationToken);
    }
}
