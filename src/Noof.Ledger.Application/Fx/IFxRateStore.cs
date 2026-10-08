using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Fx;

public sealed record FxRate(CurrencyCode Currency, DateOnly AsOfDate, decimal UnitsPerEur);

public interface IFxRateStore
{
    // Inserts every rate of the snapshot that is not stored yet; true when at least one row was inserted.
    Task<bool> AppendAsync(FxRateSnapshot snapshot, CancellationToken cancellationToken);

    // Source FxSources.OpenErApi only: every rate with as_of_date in [from, to], plus, per currency, the latest
    // before from and the earliest after to.
    Task<IReadOnlyList<FxRate>> GetAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken);

    Task<DateOnly?> NewestAsOfDateAsync(CancellationToken cancellationToken);
}
