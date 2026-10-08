namespace Noof.Ledger.Application.Reporting.Summary;

// SS-4, SS-5: a month is compared with the month before it and with the average of the three before it. The current
// month counts up to today and each earlier month over the same days, clamped to its own end; a finished month is
// compared whole.
internal static class SummaryWindows
{
    public const int ComparedMonths = 3;

    public static SummaryPeriod MonthsBack(SummaryPeriod period, int months)
    {
        var first = period.FirstDay.AddMonths(-months);
        var daysInMonth = DateTime.DaysInMonth(first.Year, first.Month);
        var days = period.Finished ? daysInMonth : Math.Min(period.LastDay.Day, daysInMonth);

        return new SummaryPeriod(first, first.AddDays(days - 1), period.Finished);
    }

    public static bool Contains(this SummaryPeriod window, DateOnly day) => day >= window.FirstDay && day <= window.LastDay;

    // A-5: only months from the scope's first month with anything counted; a later month with nothing is a real zero.
    public static int AverageMonths(SummaryPeriod period, DateOnly? historyStart) =>
        historyStart is { } start
            ? Enumerable.Range(1, ComparedMonths).Count(months => MonthsBack(period, months).FirstDay >= start)
            : 0;
}
