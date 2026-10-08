using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Fx;
using Noof.Ledger.Persistence.Fx;

namespace Noof.Ledger.Persistence.Tests;

public class FxRatesHealthCheckTests
{
    // Half an hour before midnight UTC: the age is counted in UTC days, as as_of_date is.
    static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 10, 8, 23, 30, 0, TimeSpan.Zero));

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static IDatabaseGate GateWith(DatabaseState state)
    {
        var gate = Substitute.For<IDatabaseGate>();
        gate.State.Returns(state);
        return gate;
    }

    static IFxRateStore StoreWithNewest(DateOnly? newest)
    {
        var store = Substitute.For<IFxRateStore>();
        store.NewestAsOfDateAsync(Arg.Any<CancellationToken>()).Returns(newest);
        return store;
    }

    [Fact]
    public void Reports_its_name_order_and_log_category()
    {
        var check = new FxRatesHealthCheck(StoreWithNewest(null), GateWith(DatabaseState.Ready), Clock);

        check.Name.Should().Be("Exchange rates");
        check.Order.Should().Be(100);
        check.LogCategory.Should().Be("Noof.Ledger.Host.Workers.FxRateWorker");
    }

    [Fact]
    public async Task Waits_for_the_database_without_reading_a_rate()
    {
        var store = StoreWithNewest(new DateOnly(2026, 10, 8));
        var check = new FxRatesHealthCheck(store, GateWith(DatabaseState.Waiting), Clock);

        var outcome = await check.CheckAsync(Ct);

        outcome.Should().Be(HealthOutcome.Warning("Waiting for the database"));
        await store.DidNotReceive().NewestAsOfDateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_fresh_install_with_no_rate_says_so()
    {
        var check = new FxRatesHealthCheck(StoreWithNewest(null), GateWith(DatabaseState.Ready), Clock);

        (await check.CheckAsync(Ct)).Should().Be(HealthOutcome.Warning("No exchange rates yet"));
    }

    [Theory]
    [InlineData("2026-10-08", HealthLevel.Ok, "Rates of 2026-10-08")]
    [InlineData("2026-10-05", HealthLevel.Ok, "Rates of 2026-10-05")]
    [InlineData("2026-10-04", HealthLevel.Warning, "Exchange rates 4 days old — normal while offline")]
    [InlineData("2026-09-08", HealthLevel.Warning, "Exchange rates 30 days old — normal while offline")]
    public async Task Rates_up_to_three_days_old_are_fresh_and_older_ones_say_how_old(
        string newest, HealthLevel level, string summary)
    {
        var check = new FxRatesHealthCheck(StoreWithNewest(DateOnly.Parse(newest)), GateWith(DatabaseState.Ready), Clock);

        (await check.CheckAsync(Ct)).Should().Be(new HealthOutcome(level, summary));
    }
}
