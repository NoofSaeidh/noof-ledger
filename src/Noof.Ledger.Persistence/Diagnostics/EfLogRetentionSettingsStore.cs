using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Persistence.Diagnostics;

internal sealed class EfLogRetentionSettingsStore(LedgerDbContext db, TimeProvider timeProvider) : ILogRetentionSettings
{
    const string Key = "logging.retention-days";

    public async Task<LogRetentionDays> GetAsync(CancellationToken cancellationToken)
    {
        var row = await db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == Key, cancellationToken);

        if (row is null)
            return LogRetentionDays.Default;

        try
        {
            return JsonSerializer.Deserialize<LogRetentionDays>(row.Value) ?? LogRetentionDays.Default;
        }
        catch (JsonException)
        {
            return LogRetentionDays.Default;
        }
    }

    public async Task SaveAsync(LogRetentionDays days, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var json = JsonSerializer.Serialize(days);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO app_setting (key, value, updated_at) VALUES ({Key}, {json}, {now})
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = EXCLUDED.updated_at
            """,
            cancellationToken);
    }
}
