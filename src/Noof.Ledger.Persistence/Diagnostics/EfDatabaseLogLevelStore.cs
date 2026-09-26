using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Persistence.Diagnostics;

internal sealed class EfDatabaseLogLevelStore(LedgerDbContext db, TimeProvider timeProvider) : IDatabaseLogLevelStore
{
    const string Key = "logging.database-minimum-level";
    const string OffValue = "Off";

    public async Task<DatabaseLogLevelSetting?> GetAsync(CancellationToken cancellationToken)
    {
        var row = await db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == Key, cancellationToken);

        if (row is null)
            return null;

        if (row.Value == OffValue)
            return DatabaseLogLevelSetting.Off;

        return Enum.TryParse<LogSeverity>(row.Value, out var level) && Enum.IsDefined(level)
            ? DatabaseLogLevelSetting.For(level)
            : null;
    }

    public async Task SaveAsync(DatabaseLogLevelSetting setting, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var value = setting.Level?.ToString() ?? OffValue;

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO app_setting (key, value, updated_at) VALUES ({Key}, {value}, {now})
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = EXCLUDED.updated_at
            """,
            cancellationToken);
    }
}
