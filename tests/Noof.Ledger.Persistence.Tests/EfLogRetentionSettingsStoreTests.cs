using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Persistence.Diagnostics;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfLogRetentionSettingsStoreTests(PostgresFixture fixture)
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-26T12:00:00Z");

    static EfLogRetentionSettingsStore StoreFor(LedgerDbContext db) => new(db, new FakeTimeProvider(Now));

    [Fact]
    public async Task With_nothing_saved_GetAsync_returns_the_C_sharp_defaults()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var days = await StoreFor(db).GetAsync(TestContext.Current.CancellationToken);

        days.Should().Be(LogRetentionDays.Default);
    }

    [Fact]
    public async Task Save_then_get_round_trips_every_level()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var store = StoreFor(db);
        var saved = new LogRetentionDays(Verbose: 2, Debug: 3, Information: 45, Warning: 100, Error: 120, Fatal: 365);

        await store.SaveAsync(saved, TestContext.Current.CancellationToken);
        var loaded = await store.GetAsync(TestContext.Current.CancellationToken);

        loaded.Should().Be(saved);
    }

    [Fact]
    public async Task A_second_save_replaces_the_first()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var store = StoreFor(db);

        await store.SaveAsync(new LogRetentionDays(2, 2, 2, 2, 2, 2), TestContext.Current.CancellationToken);
        await store.SaveAsync(new LogRetentionDays(9, 9, 9, 9, 9, 9), TestContext.Current.CancellationToken);
        var loaded = await store.GetAsync(TestContext.Current.CancellationToken);

        loaded.Should().Be(new LogRetentionDays(9, 9, 9, 9, 9, 9));
    }
}
