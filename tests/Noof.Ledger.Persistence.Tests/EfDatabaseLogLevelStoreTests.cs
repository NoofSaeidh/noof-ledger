using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Persistence.Diagnostics;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfDatabaseLogLevelStoreTests(PostgresFixture fixture)
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-26T12:00:00Z");

    static EfDatabaseLogLevelStore StoreFor(LedgerDbContext db, DateTimeOffset now) =>
        new(db, new FakeTimeProvider(now));

    [Fact]
    public async Task No_row_gives_null()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var level = await StoreFor(db, Now).GetAsync(TestContext.Current.CancellationToken);

        level.Should().BeNull();
    }

    [Fact]
    public async Task Save_then_get_returns_the_saved_level()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var store = StoreFor(db, Now);

        await store.SaveAsync(LogSeverity.Debug, TestContext.Current.CancellationToken);
        var level = await store.GetAsync(TestContext.Current.CancellationToken);

        level.Should().Be(LogSeverity.Debug);
    }

    [Fact]
    public async Task A_second_save_updates_the_same_row()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var later = Now.AddHours(1);

        await StoreFor(db, Now).SaveAsync(LogSeverity.Debug, TestContext.Current.CancellationToken);
        await StoreFor(db, later).SaveAsync(LogSeverity.Information, TestContext.Current.CancellationToken);

        var rows = await db.AppSettings.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        rows.Should().ContainSingle();
        rows[0].Value.Should().Be(nameof(LogSeverity.Information));
        rows[0].UpdatedAt.Should().Be(later);
    }

    [Theory]
    [InlineData("Loud")]
    [InlineData("7")]
    public async Task A_stored_value_that_is_not_a_defined_level_gives_null(string value)
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        db.AppSettings.Add(new() { Key = "logging.database-minimum-level", Value = value, UpdatedAt = Now });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var level = await StoreFor(db, Now).GetAsync(TestContext.Current.CancellationToken);

        level.Should().BeNull();
    }
}
