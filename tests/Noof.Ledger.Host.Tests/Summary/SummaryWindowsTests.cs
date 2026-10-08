using AwesomeAssertions;
using Noof.Ledger.Application.Reporting.Summary;

namespace Noof.Ledger.Host.Tests.Summary;

// Spec SS-5: the current month counts up to today and each earlier month over the same days, clamped to its end; a
// finished month is compared whole. A-5: the average counts only months from the scope's history start.
public class SummaryWindowsTests
{
    static SummaryPeriod Running(DateOnly first, DateOnly today) => new(first, today, Finished: false);

    [Fact]
    public void On_the_first_the_month_and_every_earlier_window_are_one_day()
    {
        var onTheFirst = Running(new(2026, 10, 1), new(2026, 10, 1));

        SummaryWindows.MonthsBack(onTheFirst, 0).Should().Be(onTheFirst);
        SummaryWindows.MonthsBack(onTheFirst, 1).Should().Be(Running(new(2026, 9, 1), new(2026, 9, 1)));
        SummaryWindows.MonthsBack(onTheFirst, 3).Should().Be(Running(new(2026, 7, 1), new(2026, 7, 1)));
    }

    [Fact]
    public void On_the_31st_February_is_clamped_to_its_last_day()
    {
        var march = Running(new(2026, 3, 1), new(2026, 3, 31));

        SummaryWindows.MonthsBack(march, 1).Should().Be(Running(new(2026, 2, 1), new(2026, 2, 28)));
        SummaryWindows.MonthsBack(march, 2).Should().Be(Running(new(2026, 1, 1), new(2026, 1, 31)));
        SummaryWindows.MonthsBack(march, 3).Should().Be(Running(new(2025, 12, 1), new(2025, 12, 31)));
    }

    [Fact]
    public void A_leap_February_keeps_its_29th_and_January_stops_at_the_same_day()
    {
        var march = Running(new(2028, 3, 1), new(2028, 3, 30));

        SummaryWindows.MonthsBack(march, 1).Should().Be(Running(new(2028, 2, 1), new(2028, 2, 29)));
        SummaryWindows.MonthsBack(march, 2).Should().Be(Running(new(2028, 1, 1), new(2028, 1, 30)));
    }

    [Fact]
    public void A_finished_February_compares_with_whole_months()
    {
        var february = new SummaryPeriod(new(2026, 2, 1), new(2026, 2, 28), Finished: true);

        SummaryWindows.MonthsBack(february, 1).Should().Be(new SummaryPeriod(new(2026, 1, 1), new(2026, 1, 31), true));
        SummaryWindows.MonthsBack(february, 2).Should().Be(new SummaryPeriod(new(2025, 12, 1), new(2025, 12, 31), true));
        SummaryWindows.MonthsBack(february, 3).Should().Be(new SummaryPeriod(new(2025, 11, 1), new(2025, 11, 30), true));
    }

    [Fact]
    public void A_window_holds_its_first_and_last_day_and_nothing_outside()
    {
        var window = Running(new(2026, 9, 1), new(2026, 9, 8));

        window.Contains(new DateOnly(2026, 9, 1)).Should().BeTrue();
        window.Contains(new DateOnly(2026, 9, 8)).Should().BeTrue();
        window.Contains(new DateOnly(2026, 8, 31)).Should().BeFalse();
        window.Contains(new DateOnly(2026, 9, 9)).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("2026-11-01", 0)]
    [InlineData("2026-10-01", 0)]
    [InlineData("2026-09-01", 1)]
    [InlineData("2026-08-01", 2)]
    [InlineData("2026-07-01", 3)]
    [InlineData("2025-01-01", 3)]
    public void The_average_counts_only_months_from_the_history_start(string? historyStart, int expected)
    {
        var october = new SummaryPeriod(new(2026, 10, 1), new(2026, 10, 31), Finished: true);

        SummaryWindows.AverageMonths(october, historyStart is null ? null : DateOnly.Parse(historyStart, System.Globalization.CultureInfo.InvariantCulture))
            .Should().Be(expected);
    }
}
