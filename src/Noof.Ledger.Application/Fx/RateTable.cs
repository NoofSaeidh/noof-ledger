using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Fx;

// Spec §1 "Lookup": X to Y on day d is amount / upe(X, d) × upe(Y, d), where one EUR buys upe units and upe(EUR) = 1.
// A currency's rate for d is its latest on or before d, failing that its earliest after; using one from after d, or
// more than three days before it, makes the conversion approximate. Nothing is rounded here.
internal sealed class RateTable(IReadOnlyList<FxRate> rates)
{
    const int FreshDays = 3;

    readonly ILookup<CurrencyCode, FxRate> byCurrency = rates
        .OrderBy(rate => rate.AsOfDate)
        .ToLookup(rate => rate.Currency);

    public bool IsEmpty => byCurrency.Count == 0;

    public RateConversion? TryConvert(decimal amount, CurrencyCode from, CurrencyCode to, DateOnly day)
    {
        if (from == to)
            return new RateConversion(amount, false, null, null);

        if (UnitsPerEur(from, day) is not { } source || UnitsPerEur(to, day) is not { } target)
            return null;

        DateOnly?[] dates = [source.AsOf, target.AsOf];
        var used = dates.OfType<DateOnly>().ToList();

        return new RateConversion(
            amount / source.Units * target.Units,
            used.Exists(date => date > day || day.DayNumber - date.DayNumber > FreshDays),
            used.Min(),
            used.Max());
    }

    UnitsOn? UnitsPerEur(CurrencyCode currency, DateOnly day)
    {
        if (currency == CurrencyCode.Eur)
            return new UnitsOn(1m, null);

        var known = byCurrency[currency];
        var rate = known.LastOrDefault(candidate => candidate.AsOfDate <= day)
            ?? known.FirstOrDefault(candidate => candidate.AsOfDate > day);

        return rate is null ? null : new UnitsOn(rate.UnitsPerEur, rate.AsOfDate);
    }

    readonly record struct UnitsOn(decimal Units, DateOnly? AsOf);
}

internal readonly record struct RateConversion(decimal Amount, bool Approximate, DateOnly? OldestRateDate, DateOnly? NewestRateDate);
