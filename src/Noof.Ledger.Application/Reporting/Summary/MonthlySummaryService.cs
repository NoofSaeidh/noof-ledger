using Noof.Ledger.Application.Fx;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Reporting.Summary;

// "Now" and the local month come from the clock and the zone records are dated in (Capture:TimeZone), the same pair
// EfSpendingReadModel uses, so Home's This month and the summary agree on which month it is.
internal sealed class MonthlySummaryService(
    ISummaryRowsReader reader, IFxRateStore rateStore, TimeProvider timeProvider, TimeZoneInfo zone) : IMonthlySummaryService
{
    public DateOnly CurrentMonth()
    {
        var today = Today();
        return new DateOnly(today.Year, today.Month, 1);
    }

    public async Task<MonthlySummary> BuildAsync(
        DateOnly firstDayOfMonth, SummaryScope scope, CurrencyCode currency, CancellationToken cancellationToken)
    {
        var today = Today();
        var currentMonth = new DateOnly(today.Year, today.Month, 1);
        if (firstDayOfMonth.Day != 1 || firstDayOfMonth > currentMonth)
            throw new ArgumentOutOfRangeException(
                nameof(firstDayOfMonth), firstDayOfMonth, "A summary is of a month's first day, the current month's or an earlier one.");
        if (scope.IsAllWallets && !ReportingCurrencies.All.Contains(currency))
            throw new ArgumentOutOfRangeException(nameof(currency), currency, "All wallets are totalled in EUR or RSD only.");

        var period = firstDayOfMonth == currentMonth
            ? new SummaryPeriod(firstDayOfMonth, today, Finished: false)
            : new SummaryPeriod(firstDayOfMonth, firstDayOfMonth.AddMonths(1).AddDays(-1), Finished: true);
        var from = firstDayOfMonth.AddMonths(-SummaryWindows.ComparedMonths);

        var rows = await reader.ReadAsync(from, period.LastDay.AddDays(1), cancellationToken);
        var rates = await rateStore.GetAsync(from, period.LastDay, cancellationToken);

        return MonthlySummaryCalculator.Calculate(rows, new RateTable(rates), period, scope, currency);
    }

    DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), zone).DateTime);
}
