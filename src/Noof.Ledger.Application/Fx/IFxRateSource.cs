using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Fx;

// UnitsPerEur holds every supported currency but EUR: how many units of it one EUR buys.
public sealed record FxRateSnapshot(DateOnly AsOfDate, string Source, IReadOnlyDictionary<CurrencyCode, decimal> UnitsPerEur);

public interface IFxRateSource
{
    // Null on any failure — HTTP, timeout, an invalid payload — which the source has already logged.
    Task<FxRateSnapshot?> FetchLatestAsync(CancellationToken cancellationToken);
}
