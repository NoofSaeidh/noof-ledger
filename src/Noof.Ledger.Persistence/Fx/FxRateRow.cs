using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Fx;

internal sealed class FxRateRow
{
    public required CurrencyCode Currency { get; init; }
    public required DateOnly AsOfDate { get; init; }
    public required string Source { get; init; }
    public required decimal UnitsPerEur { get; init; }
    public required DateTimeOffset FetchedAt { get; init; }
}
