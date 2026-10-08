using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Reporting.Summary;

namespace Noof.Ledger.Persistence.Settings;

internal sealed class EfAutoSummaryMarker(LedgerDbContext db, TimeProvider timeProvider) : IAutoSummaryMarker
{
    const string SentKey = "summary.last-auto-month";
    const string WaitingKey = "summary.auto-waiting-since";
    const string MonthFormat = "yyyy-MM";
    const string InstantFormat = "O";

    public async Task<DateOnly?> GetAsync(CancellationToken cancellationToken) =>
        DateOnly.TryParseExact(
            await ValueOfAsync(SentKey, cancellationToken), MonthFormat, CultureInfo.InvariantCulture, DateTimeStyles.None,
            out var month)
            ? month
            : null;

    public Task SetAsync(DateOnly firstDayOfMonth, CancellationToken cancellationToken) =>
        UpsertAsync(SentKey, MonthText(firstDayOfMonth), cancellationToken);

    // One month waits at a time: a stamp left by another month says nothing about this one.
    public async Task<DateTimeOffset?> GetWaitingSinceAsync(DateOnly firstDayOfMonth, CancellationToken cancellationToken) =>
        (await ValueOfAsync(WaitingKey, cancellationToken))?.Split('|') is [var month, var since]
        && month == MonthText(firstDayOfMonth)
        && DateTimeOffset.TryParseExact(since, InstantFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? at
            : null;

    public Task SetWaitingSinceAsync(DateOnly firstDayOfMonth, DateTimeOffset since, CancellationToken cancellationToken) =>
        UpsertAsync(
            WaitingKey,
            $"{MonthText(firstDayOfMonth)}|{since.ToUniversalTime().ToString(InstantFormat, CultureInfo.InvariantCulture)}",
            cancellationToken);

    static string MonthText(DateOnly firstDayOfMonth) => firstDayOfMonth.ToString(MonthFormat, CultureInfo.InvariantCulture);

    Task<string?> ValueOfAsync(string key, CancellationToken cancellationToken) =>
        db.AppSettings.AsNoTracking()
            .Where(setting => setting.Key == key)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(cancellationToken);

    async Task UpsertAsync(string key, string value, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO app_setting (key, value, updated_at) VALUES ({key}, {value}, {now})
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = EXCLUDED.updated_at
            """,
            cancellationToken);
    }
}
