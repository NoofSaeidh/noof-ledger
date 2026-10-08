using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Reporting.Summary;

public static class ReportingCurrencies
{
    public static IReadOnlyList<CurrencyCode> All { get; } = [CurrencyCode.Eur, CurrencyCode.Rsd];
}

public interface IReportingCurrencySetting
{
    // EUR when nothing usable is stored.
    Task<CurrencyCode> GetAsync(CancellationToken cancellationToken);

    // Throws ArgumentOutOfRangeException for anything but ReportingCurrencies.All.
    Task SaveAsync(CurrencyCode currency, CancellationToken cancellationToken);
}
