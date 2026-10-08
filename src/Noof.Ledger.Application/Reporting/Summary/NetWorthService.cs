using Noof.Ledger.Application.Fx;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Reporting.Summary;

// Spec §2 "Net worth" (SS-13): every balance IBalanceReadModel returns - archived wallets and a wallet's other
// currencies included, since that is money held - at the latest rate on or before today in the zone records are dated in.
internal sealed class NetWorthService(
    IBalanceReadModel balances, IFxRateStore rateStore, TimeProvider timeProvider, TimeZoneInfo zone) : INetWorthService
{
    public async Task<NetWorth> GetAsync(CurrencyCode currency, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), zone).DateTime);
        var wallets = await balances.BalancesAsync(cancellationToken);
        // The source may store a rate dated up to a day ahead, and RateTable falls back to a later rate when there is
        // none before; net worth takes only rates on or before today, so a currency with only a later one is left out.
        var stored = await rateStore.GetAsync(today, today, cancellationToken);
        var rates = new RateTable([.. stored.Where(rate => rate.AsOfDate <= today)]);
        var held = wallets
            .SelectMany(wallet => wallet.Balances.Select(balance => new Held(
                wallet.Archived, balance, rates.TryConvert(balance.Amount, balance.Currency, currency, today))))
            .ToList();

        return new NetWorth(
            currency,
            held.Sum(item => item.Converted?.Amount ?? 0m),
            held.Select(item => item.Converted?.NewestRateDate).Max(),
            [.. held.Where(item => item.Converted is null && item.Balance.Amount != 0).Select(item => item.Balance.Currency).Distinct().Order()],
            held.Exists(item => item.Archived && item.Balance.Amount != 0));
    }

    sealed record Held(bool Archived, Money Balance, RateConversion? Converted);
}
