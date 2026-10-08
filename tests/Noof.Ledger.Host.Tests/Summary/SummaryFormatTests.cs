using System.Globalization;
using AwesomeAssertions;
using Noof.Ledger.Application.Reporting.Summary;

namespace Noof.Ledger.Host.Tests.Summary;

// Spec P-8: the bot's and the model's figures are two decimals with no grouping, and the dates are invariant.
public class SummaryFormatTests
{
    static decimal Parse(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("1234.5", "1234.50")]
    [InlineData("2.345", "2.35")]
    [InlineData("0.005", "0.01")]
    [InlineData("-0.005", "-0.01")]
    [InlineData("-0.001", "0.00")]
    [InlineData("99999999.99", "99999999.99")]
    public void An_amount_is_two_decimals_half_away_from_zero_without_grouping(string amount, string expected) =>
        SummaryFormat.Amount(Parse(amount)).Should().Be(expected);

    [Theory]
    [InlineData("12", "+12.00")]
    [InlineData("-3.4", "-3.40")]
    [InlineData("0", "0.00")]
    [InlineData("0.004", "0.00")]
    [InlineData("-0.004", "0.00")]
    [InlineData("255.825", "+255.83")]
    public void A_signed_amount_has_a_plus_only_above_zero(string amount, string expected) =>
        SummaryFormat.Signed(Parse(amount)).Should().Be(expected);

    [Fact]
    public void A_day_is_day_and_short_month_and_a_month_is_its_name_and_year()
    {
        SummaryFormat.Day(new DateOnly(2026, 10, 7)).Should().Be("7 Oct");
        SummaryFormat.Month(new DateOnly(2026, 10, 1)).Should().Be("October 2026");
    }

    [Fact]
    public void A_window_is_its_days_while_running_and_the_month_once_finished()
    {
        SummaryFormat.Window(new SummaryPeriod(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 8), false)).Should().Be("1–8 Oct");
        SummaryFormat.Window(new SummaryPeriod(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 1), false)).Should().Be("1 Oct");
        SummaryFormat.Window(new SummaryPeriod(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), true)).Should().Be("October");
    }

    [Fact]
    public void Formats_ignore_the_server_culture()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ru-RU");

            SummaryFormat.Amount(1234.5m).Should().Be("1234.50");
            SummaryFormat.Signed(-3.4m).Should().Be("-3.40");
            SummaryFormat.Day(new DateOnly(2026, 10, 7)).Should().Be("7 Oct");
            SummaryFormat.Month(new DateOnly(2026, 10, 1)).Should().Be("October 2026");
            SummaryFormat.Window(new SummaryPeriod(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), true)).Should().Be("October");
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }
}
