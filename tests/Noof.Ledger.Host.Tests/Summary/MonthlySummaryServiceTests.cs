using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Noof.Ledger.Application.Fx;
using Noof.Ledger.Application.Reporting.Summary;
using Noof.Ledger.Domain;
using static Noof.Ledger.Host.Tests.Summary.SummaryRowsBuilder;

namespace Noof.Ledger.Host.Tests.Summary;

public class MonthlySummaryServiceTests
{
    // Fixed UTC+2 with no daylight saving: a window test never depends on the machine's tz database.
    static readonly TimeZoneInfo PlusTwo = TimeZoneInfo.CreateCustomTimeZone("Test+02", TimeSpan.FromHours(2), "Test+02", "Test+02");

    readonly ISummaryRowsReader reader = Substitute.For<ISummaryRowsReader>();
    readonly IFxRateStore rateStore = Substitute.For<IFxRateStore>();
    readonly FakeTimeProvider clock = new();

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    MonthlySummaryService Service() => new(reader, rateStore, clock, PlusTwo);

    void Reads(SummaryRows rows) =>
        reader.ReadAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(rows);

    void Stores(params FxRate[] rates) =>
        rateStore.GetAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(rates);

    [Fact]
    public void The_current_month_turns_at_the_zones_midnight_not_at_UTCs()
    {
        clock.SetUtcNow(new DateTimeOffset(2026, 9, 30, 21, 59, 0, TimeSpan.Zero));   // 23:59 on 30 Sep, local
        Service().CurrentMonth().Should().Be(new DateOnly(2026, 9, 1));

        clock.SetUtcNow(new DateTimeOffset(2026, 9, 30, 22, 30, 0, TimeSpan.Zero));   // 00:30 on 1 Oct, local
        Service().CurrentMonth().Should().Be(new DateOnly(2026, 10, 1));
    }

    [Fact]
    public async Task On_the_first_the_current_month_is_a_one_day_window_read_with_the_three_months_before()
    {
        clock.SetUtcNow(new DateTimeOffset(2026, 9, 30, 22, 30, 0, TimeSpan.Zero));
        var rows = new SummaryRowsBuilder().Wallet(WiseId, "Wise", CurrencyCode.Eur, new DateOnly(2026, 7, 1));
        rows.Expense(WiseId, new DateOnly(2026, 10, 1), "Maxi", Line("Groceries", 12.00m));
        rows.Expense(WiseId, new DateOnly(2026, 9, 1), "Maxi", Line("Groceries", 8.00m));
        rows.Expense(WiseId, new DateOnly(2026, 9, 2), "Maxi", Line("Groceries", 99.00m));
        Reads(rows.Build());
        Stores();

        var summary = await Service().BuildAsync(new DateOnly(2026, 10, 1), SummaryScope.AllWallets, CurrencyCode.Eur, Ct);

        summary.Period.Should().Be(new SummaryPeriod(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 1), Finished: false));
        // Windows: 1 Oct; 1 Sep (8.00 - the 2nd is outside); 1 Aug and 1 Jul (0): average (8.00 + 0 + 0) / 3.
        summary.Spent.Should().Be(new SummaryAmount(12.00m, 8.00m, 8.00m / 3m));
        await reader.Received(1).ReadAsync(new DateOnly(2026, 7, 1), new DateOnly(2026, 10, 2), Arg.Any<CancellationToken>());
        await rateStore.Received(1).GetAsync(new DateOnly(2026, 7, 1), new DateOnly(2026, 10, 1), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_finished_February_is_read_whole_with_whole_months_before_it()
    {
        clock.SetUtcNow(new DateTimeOffset(2026, 3, 10, 10, 0, 0, TimeSpan.Zero));
        var rows = new SummaryRowsBuilder().Wallet(WiseId, "Wise", CurrencyCode.Eur, new DateOnly(2025, 11, 1));
        rows.Expense(WiseId, new DateOnly(2026, 1, 30), "Maxi", Line("Groceries", 10.00m));
        rows.Expense(WiseId, new DateOnly(2026, 1, 31), "Maxi", Line("Groceries", 5.00m));
        rows.Expense(WiseId, new DateOnly(2026, 2, 28), "Maxi", Line("Groceries", 7.00m));
        Reads(rows.Build());
        Stores();

        var summary = await Service().BuildAsync(new DateOnly(2026, 2, 1), SummaryScope.AllWallets, CurrencyCode.Eur, Ct);

        summary.Period.Should().Be(new SummaryPeriod(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28), Finished: true));
        // Whole January 10.00 + 5.00 = 15.00; December and November 0: average 15.00 / 3 = 5.00.
        summary.Spent.Should().Be(new SummaryAmount(7.00m, 15.00m, 5.00m));
        await reader.Received(1).ReadAsync(new DateOnly(2025, 11, 1), new DateOnly(2026, 3, 1), Arg.Any<CancellationToken>());
        await rateStore.Received(1).GetAsync(new DateOnly(2025, 11, 1), new DateOnly(2026, 2, 28), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_record_at_half_past_midnight_on_the_first_takes_that_days_rate()
    {
        clock.SetUtcNow(new DateTimeOffset(2026, 9, 30, 22, 30, 0, TimeSpan.Zero));
        var rows = new SummaryRowsBuilder().Wallet(CashRsdId, "Cash RSD", CurrencyCode.Rsd, new DateOnly(2026, 10, 1));
        rows.Expense(CashRsdId, new DateOnly(2026, 10, 1), "Maxi", Line("Groceries", 1170.00m, CurrencyCode.Rsd));
        Reads(rows.Build());
        Stores(new FxRate(CurrencyCode.Rsd, new DateOnly(2026, 9, 30), 117.20m), new FxRate(CurrencyCode.Rsd, new DateOnly(2026, 10, 1), 117.00m));

        var summary = await Service().BuildAsync(new DateOnly(2026, 10, 1), SummaryScope.AllWallets, CurrencyCode.Eur, Ct);

        // The record is dated 1 Oct locally; its rate is 1 Oct's: 1170.00 / 117.00 = 10.00 (the UTC day, 30 Sep, would
        // give 1170.00 / 117.20 = 9.98).
        summary.Spent.Amount.Should().Be(10.00m);
        summary.NewestRateDate.Should().Be(new DateOnly(2026, 10, 1));
        summary.AnyApproximate.Should().BeFalse();
    }

    [Fact]
    public async Task A_month_after_the_current_one_or_a_day_other_than_the_first_is_refused_before_reading()
    {
        clock.SetUtcNow(new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero));

        var next = () => Service().BuildAsync(new DateOnly(2026, 11, 1), SummaryScope.AllWallets, CurrencyCode.Eur, Ct);
        // A past month's 15th, so only the first-day rule refuses it.
        var midMonth = () => Service().BuildAsync(new DateOnly(2026, 9, 15), SummaryScope.AllWallets, CurrencyCode.Eur, Ct);

        await next.Should().ThrowAsync<ArgumentOutOfRangeException>().WithParameterName("firstDayOfMonth");
        await midMonth.Should().ThrowAsync<ArgumentOutOfRangeException>().WithParameterName("firstDayOfMonth");
        await reader.DidNotReceiveWithAnyArgs().ReadAsync(default, default, Ct);
    }

    [Fact]
    public async Task Only_EUR_and_RSD_total_all_wallets_and_a_wallet_keeps_its_own_currency()
    {
        clock.SetUtcNow(new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero));
        Reads(new SummaryRowsBuilder().Wallet(KaspiId, "Kaspi", CurrencyCode.Kzt, null).Build());
        Stores();

        var inUsd = () => Service().BuildAsync(new DateOnly(2026, 10, 1), SummaryScope.AllWallets, CurrencyCode.Usd, Ct);
        await inUsd.Should().ThrowAsync<ArgumentOutOfRangeException>().WithParameterName("currency");

        var kaspi = await Service().BuildAsync(new DateOnly(2026, 10, 1), new SummaryScope(KaspiId), CurrencyCode.Usd, Ct);
        kaspi.Currency.Should().Be(CurrencyCode.Kzt);
        kaspi.WalletName.Should().Be("Kaspi");
    }

    [Fact]
    public async Task An_unknown_wallet_is_KeyNotFound()
    {
        clock.SetUtcNow(new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero));
        Reads(new SummaryRowsBuilder().Wallet(WiseId, "Wise", CurrencyCode.Eur, null).Build());
        Stores();

        var act = () => Service().BuildAsync(new DateOnly(2026, 10, 1), new SummaryScope(Guid.NewGuid()), CurrencyCode.Eur, Ct);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }
}
