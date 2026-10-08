using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Reporting.Summary;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Settings;

internal sealed class EfReportingCurrencySetting(LedgerDbContext db, TimeProvider timeProvider) : IReportingCurrencySetting
{
    const string Key = "reporting.currency";

    public async Task<CurrencyCode> GetAsync(CancellationToken cancellationToken)
    {
        var stored = await db.AppSettings.AsNoTracking()
            .Where(setting => setting.Key == Key)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(cancellationToken);

        return ReportingCurrencies.All.FirstOrDefault(currency => currency.Value == stored, CurrencyCode.Eur);
    }

    public async Task SaveAsync(CurrencyCode currency, CancellationToken cancellationToken)
    {
        if (!ReportingCurrencies.All.Contains(currency))
            throw new ArgumentOutOfRangeException(nameof(currency), currency, "Summaries are reported in EUR or RSD only.");

        var now = timeProvider.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO app_setting (key, value, updated_at) VALUES ({Key}, {currency.Value}, {now})
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = EXCLUDED.updated_at
            """,
            cancellationToken);
    }
}
