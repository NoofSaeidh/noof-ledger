using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Fx;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Fx;
using Npgsql;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfFxRateStoreTests(PostgresFixture fixture)
{
    static readonly DateTimeOffset Now = new(2026, 10, 8, 6, 0, 0, TimeSpan.Zero);
    static readonly DateOnly Day = new(2026, 10, 7);

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static EfFxRateStore StoreFor(LedgerDbContext db) => new(db, new FakeTimeProvider(Now));

    static FxRateSnapshot Snapshot(DateOnly day, params (CurrencyCode Currency, decimal UnitsPerEur)[] rates) =>
        new(day, FxSources.OpenErApi, rates.ToDictionary(rate => rate.Currency, rate => rate.UnitsPerEur));

    static FxRateSnapshot FullSnapshot(DateOnly day) => Snapshot(day,
        (CurrencyCode.Rsd, 117.15m), (CurrencyCode.Usd, 1.085012345678m), (CurrencyCode.Rub, 98.4m), (CurrencyCode.Kzt, 565.2m));

    [Fact]
    public async Task Append_stores_every_rate_of_the_snapshot_at_full_precision_with_the_time_it_was_fetched()
    {
        await using var db = await fixture.CreateMigratedContextAsync();

        (await StoreFor(db).AppendAsync(FullSnapshot(Day), Ct)).Should().BeTrue();

        var rows = await db.FxRates.AsNoTracking().ToListAsync(Ct);
        rows.Select(row => (row.Currency, row.AsOfDate, row.Source, row.UnitsPerEur, row.FetchedAt)).Should().BeEquivalentTo(
        [
            (CurrencyCode.Rsd, Day, FxSources.OpenErApi, 117.15m, Now),
            (CurrencyCode.Usd, Day, FxSources.OpenErApi, 1.085012345678m, Now),
            (CurrencyCode.Rub, Day, FxSources.OpenErApi, 98.4m, Now),
            (CurrencyCode.Kzt, Day, FxSources.OpenErApi, 565.2m, Now),
        ]);
    }

    [Fact]
    public async Task A_repeat_of_a_stored_day_inserts_nothing_and_keeps_the_first_rates()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var store = StoreFor(db);
        await store.AppendAsync(FullSnapshot(Day), Ct);

        var repeated = await store.AppendAsync(Snapshot(Day, (CurrencyCode.Rsd, 999m)), Ct);

        repeated.Should().BeFalse("the archive is append-only: a day already stored is never superseded");
        (await db.FxRates.AsNoTracking().CountAsync(Ct)).Should().Be(4);
        (await db.FxRates.AsNoTracking().SingleAsync(row => row.Currency == CurrencyCode.Rsd, Ct))
            .UnitsPerEur.Should().Be(117.15m);
    }

    [Fact]
    public async Task A_new_day_is_appended_beside_the_days_already_stored()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var store = StoreFor(db);
        await store.AppendAsync(FullSnapshot(Day), Ct);

        (await store.AppendAsync(FullSnapshot(Day.AddDays(1)), Ct)).Should().BeTrue();

        (await db.FxRates.AsNoTracking().CountAsync(Ct)).Should().Be(8);
    }

    [Fact]
    public async Task A_snapshot_holding_a_rate_that_is_not_positive_stores_nothing_at_all()
    {
        await using var db = await fixture.CreateMigratedContextAsync();

        var act = () => StoreFor(db).AppendAsync(Snapshot(Day, (CurrencyCode.Rsd, 117.15m), (CurrencyCode.Usd, 0m)), Ct);

        (await act.Should().ThrowAsync<PostgresException>()).Which.ConstraintName
            .Should().Be("ck_fx_rates_units_per_eur_positive");
        (await db.FxRates.AsNoTracking().CountAsync(Ct)).Should().Be(0, "a snapshot is one statement: stored whole or not at all");
    }

    [Fact]
    public async Task The_table_refuses_a_rate_for_EUR_itself()
    {
        await using var db = await fixture.CreateMigratedContextAsync();

        var act = () => StoreFor(db).AppendAsync(Snapshot(Day, (CurrencyCode.Eur, 1m)), Ct);

        (await act.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("ck_fx_rates_currency_not_eur");
    }

    [Fact]
    public async Task GetAsync_returns_the_range_and_the_nearest_rate_on_each_side_of_it_per_currency()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var store = StoreFor(db);
        DateOnly[] rsdDays = [new(2026, 9, 25), new(2026, 9, 28), new(2026, 10, 1), new(2026, 10, 3), new(2026, 10, 6), new(2026, 10, 9)];
        foreach (var day in rsdDays)
            await store.AppendAsync(Snapshot(day, (CurrencyCode.Rsd, 117m + day.Day / 100m)), Ct);
        await store.AppendAsync(Snapshot(new DateOnly(2026, 9, 20), (CurrencyCode.Usd, 1.08m)), Ct);
        await store.AppendAsync(Snapshot(new DateOnly(2026, 10, 10), (CurrencyCode.Usd, 1.09m)), Ct);

        var rates = await store.GetAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 6), Ct);

        rates.Should().Equal(
            new FxRate(CurrencyCode.Rsd, new DateOnly(2026, 9, 28), 117.28m),
            new FxRate(CurrencyCode.Rsd, new DateOnly(2026, 10, 1), 117.01m),
            new FxRate(CurrencyCode.Rsd, new DateOnly(2026, 10, 3), 117.03m),
            new FxRate(CurrencyCode.Rsd, new DateOnly(2026, 10, 6), 117.06m),
            new FxRate(CurrencyCode.Rsd, new DateOnly(2026, 10, 9), 117.09m),
            new FxRate(CurrencyCode.Usd, new DateOnly(2026, 9, 20), 1.08m),
            new FxRate(CurrencyCode.Usd, new DateOnly(2026, 10, 10), 1.09m));
    }

    [Fact]
    public async Task NewestAsOfDateAsync_is_null_until_a_rate_is_stored_then_the_latest_day()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var store = StoreFor(db);

        (await store.NewestAsOfDateAsync(Ct)).Should().BeNull();

        await store.AppendAsync(FullSnapshot(Day), Ct);
        await store.AppendAsync(FullSnapshot(Day.AddDays(-3)), Ct);

        (await store.NewestAsOfDateAsync(Ct)).Should().Be(Day);
    }

    [Fact]
    public async Task Only_the_open_er_api_source_is_read()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var store = StoreFor(db);
        await store.AppendAsync(Snapshot(Day, (CurrencyCode.Rsd, 117.15m)), Ct);
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO fx_rates (currency, as_of_date, source, units_per_eur, fetched_at)
            VALUES ('RSD', {Day.AddDays(1)}, 'nbs', 117.50, {Now}), ('RSD', {Day}, 'nbs', 118.00, {Now})
            """, Ct);

        (await store.NewestAsOfDateAsync(Ct)).Should().Be(Day);
        (await store.GetAsync(Day, Day.AddDays(1), Ct)).Should().Equal(new FxRate(CurrencyCode.Rsd, Day, 117.15m));
    }
}
