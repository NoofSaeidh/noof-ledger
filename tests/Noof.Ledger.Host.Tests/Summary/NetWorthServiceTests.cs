using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Noof.Ledger.Application.Fx;
using Noof.Ledger.Application.Reporting;
using Noof.Ledger.Application.Reporting.Summary;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Host.Tests.Summary;

public class NetWorthServiceTests
{
    static readonly TimeZoneInfo PlusTwo = TimeZoneInfo.CreateCustomTimeZone("Test+02", TimeSpan.FromHours(2), "Test+02", "Test+02");
    static readonly CurrencyCode Eur = CurrencyCode.Eur;
    static readonly CurrencyCode Rsd = CurrencyCode.Rsd;
    static readonly CurrencyCode Kzt = CurrencyCode.Kzt;
    static readonly CurrencyCode Usd = CurrencyCode.Usd;
    static readonly DateOnly Today = new(2026, 10, 8);

    readonly IBalanceReadModel balances = Substitute.For<IBalanceReadModel>();
    readonly IFxRateStore rateStore = Substitute.For<IFxRateStore>();
    readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero));

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    NetWorthService Service() => new(balances, rateStore, clock, PlusTwo);

    static WalletBalance Wallet(string name, CurrencyCode currency, bool archived, params Money[] held) =>
        new(Guid.NewGuid(), name, currency, archived, held, null);

    void Holding(params WalletBalance[] wallets) => balances.BalancesAsync(Arg.Any<CancellationToken>()).Returns(wallets);

    void Rates(params FxRate[] rates) =>
        rateStore.GetAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(rates);

    void HoldingTheUsual() => Holding(
        Wallet("Wise", Eur, false, new Money(1200.50m, Eur)),
        Wallet("Cash RSD", Rsd, false, new Money(23400.00m, Rsd)),
        Wallet("Old Revolut", Eur, true, new Money(900.00m, Eur)),
        Wallet("Kaspi", Kzt, false, new Money(52000.00m, Kzt), new Money(30.00m, Usd)));

    [Fact]
    public async Task Every_balance_counts_at_todays_rate_archived_wallets_and_foreign_lines_included()
    {
        HoldingTheUsual();
        Rates(new FxRate(Rsd, Today, 117.00m), new FxRate(Kzt, Today, 520.00m));

        var worth = await Service().GetAsync(Eur, Ct);

        // 1200.50 + 23400.00 / 117.00 (200.00) + 900.00 + 52000.00 / 520.00 (100.00) = 2400.50; USD has no rate.
        worth.Should().BeEquivalentTo(new NetWorth(Eur, 2400.50m, Today, [Usd], IncludesArchived: true));
    }

    [Fact]
    public async Task In_RSD_the_same_balances_convert_the_other_way()
    {
        HoldingTheUsual();
        Rates(new FxRate(Rsd, Today, 117.00m), new FxRate(Kzt, Today, 520.00m));

        var worth = await Service().GetAsync(Rsd, Ct);

        // 1200.50 × 117 = 140458.50; 23400.00; 900.00 × 117 = 105300.00; 52000.00 / 520 × 117 = 11700.00 → 280858.50.
        worth.Total.Should().Be(280858.50m);
        worth.Currency.Should().Be(Rsd);
    }

    [Fact]
    public async Task With_no_rate_at_all_only_the_target_currency_counts_and_the_rest_is_named()
    {
        HoldingTheUsual();
        Rates();

        var worth = await Service().GetAsync(Eur, Ct);

        worth.Total.Should().Be(2100.50m);                        // 1200.50 + 900.00
        worth.Excluded.Should().Equal(Kzt, Rsd, Usd);
        worth.RatesAsOf.Should().BeNull();
    }

    [Fact]
    public async Task The_latest_rate_on_or_before_today_counts_not_a_later_one()
    {
        HoldingTheUsual();
        Rates(
            new FxRate(Rsd, new DateOnly(2026, 10, 6), 117.00m),
            new FxRate(Rsd, new DateOnly(2026, 10, 9), 118.00m),
            new FxRate(Kzt, Today, 520.00m),
            new FxRate(Usd, Today, 1.20m));

        var worth = await Service().GetAsync(Eur, Ct);

        // RSD at the 6th's 117.00 (200.00, not 23400 / 118 = 198.31); 30.00 USD / 1.20 = 25.00:
        // 1200.50 + 200.00 + 900.00 + 100.00 + 25.00 = 2425.50.
        worth.Total.Should().Be(2425.50m);
        worth.Excluded.Should().BeEmpty();
        worth.RatesAsOf.Should().Be(Today);
    }

    [Fact]
    public async Task A_currency_whose_only_rate_is_dated_after_today_is_left_out()
    {
        HoldingTheUsual();
        Rates(
            new FxRate(Rsd, Today, 117.00m),
            new FxRate(Kzt, Today, 520.00m),
            new FxRate(Usd, Today.AddDays(1), 1.20m));

        var worth = await Service().GetAsync(Eur, Ct);

        // Tomorrow's USD rate is not used: 1200.50 + 200.00 + 900.00 + 100.00 = 2400.50, USD named instead.
        worth.Should().BeEquivalentTo(new NetWorth(Eur, 2400.50m, Today, [Usd], IncludesArchived: true));
    }

    [Fact]
    public async Task Today_is_the_configured_zones_day()
    {
        clock.AdjustTime(new DateTimeOffset(2026, 10, 7, 23, 30, 0, TimeSpan.Zero));   // 01:30 on 8 Oct, local
        HoldingTheUsual();
        Rates();

        await Service().GetAsync(Eur, Ct);

        await rateStore.Received(1).GetAsync(Today, Today, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_empty_archived_wallet_and_a_zero_balance_without_a_rate_say_nothing()
    {
        Holding(
            Wallet("Wise", Eur, false, new Money(1200.50m, Eur)),
            Wallet("Old Revolut", Eur, true, new Money(0m, Eur)),
            Wallet("Kaspi", Kzt, false, new Money(52000.00m, Kzt), new Money(0m, Usd)));
        Rates(new FxRate(Kzt, Today, 520.00m));

        var worth = await Service().GetAsync(Eur, Ct);

        worth.Total.Should().Be(1300.50m);                        // 1200.50 + 52000.00 / 520.00
        worth.IncludesArchived.Should().BeFalse();
        worth.Excluded.Should().BeEmpty();
    }
}
