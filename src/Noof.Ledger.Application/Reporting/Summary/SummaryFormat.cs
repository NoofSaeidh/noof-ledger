using System.Globalization;

namespace Noof.Ledger.Application.Reporting.Summary;

// Spec P-8: every figure the bot and the model see, in the invariant culture, so one text reads the same whatever
// culture the host runs under.
internal static class SummaryFormat
{
    public static string Amount(decimal amount) => Rounded(amount).ToString("F2", CultureInfo.InvariantCulture);

    public static string Signed(decimal amount) => Rounded(amount) switch
    {
        > 0m and var positive => "+" + positive.ToString("F2", CultureInfo.InvariantCulture),
        < 0m and var negative => negative.ToString("F2", CultureInfo.InvariantCulture),
        _ => "0.00",
    };

    public static string Day(DateOnly day) => day.ToString("d MMM", CultureInfo.InvariantCulture);

    public static string Month(DateOnly day) => day.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

    public static string Window(SummaryPeriod period) => period switch
    {
        { Finished: true } => period.FirstDay.ToString("MMMM", CultureInfo.InvariantCulture),
        _ when period.FirstDay == period.LastDay => Day(period.LastDay),
        _ => string.Create(CultureInfo.InvariantCulture, $"{period.FirstDay.Day}–{Day(period.LastDay)}"),
    };

    // Money.Round's rule - two decimals, a midpoint away from zero - without a currency to build a Money from.
    static decimal Rounded(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
}
