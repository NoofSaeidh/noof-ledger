using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Reporting.Summary;

// Total over every balance IBalanceReadModel returns, archived wallets and foreign-currency lines included, each at
// the latest rate on or before today; Excluded names the currencies with no rate at all.
public sealed record NetWorth(CurrencyCode Currency, decimal Total, DateOnly? RatesAsOf, IReadOnlyList<CurrencyCode> Excluded, bool IncludesArchived);

public interface INetWorthService
{
    Task<NetWorth> GetAsync(CurrencyCode currency, CancellationToken cancellationToken);
}
