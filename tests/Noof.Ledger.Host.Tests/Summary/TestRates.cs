using Noof.Ledger.Application.Fx;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Host.Tests.Summary;

internal static class TestRates
{
    // One rate per currency for every day in [from, to], as the archive holds them after daily fetches.
    public static List<FxRate> Daily(DateOnly from, DateOnly to, params (CurrencyCode Currency, decimal Units)[] rates) =>
    [
        .. Enumerable.Range(0, to.DayNumber - from.DayNumber + 1)
            .SelectMany(offset => rates.Select(rate => new FxRate(rate.Currency, from.AddDays(offset), rate.Units))),
    ];

    // 1 EUR = 117.00 RSD = 520.00 KZT = 1.10 USD = 100.00 RUB on every day.
    public static RateTable Usual(DateOnly from, DateOnly to) => new(Daily(
        from, to, (CurrencyCode.Rsd, 117.00m), (CurrencyCode.Kzt, 520.00m), (CurrencyCode.Usd, 1.10m), (CurrencyCode.Rub, 100.00m)));
}
