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

    // A row saved before a level existed (or hand-edited) deserialises a missing/zero/negative
    // member as 0 or less - GetAsync must not hand that straight to EfLogRetention, which would
    // then delete every row of that level every prune.
    [Fact]
    public async Task A_stored_row_with_an_out_of_range_member_falls_back_to_Default_for_just_that_level()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var partialRow = """{"Verbose":2,"Debug":3,"Information":45,"Warning":-5}""";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO app_setting (key, value, updated_at)
            VALUES ('logging.retention-days', {partialRow}, {Now})
            """,
            TestContext.Current.CancellationToken);

        var loaded = await StoreFor(db).GetAsync(TestContext.Current.CancellationToken);

        loaded.Should().Be(new LogRetentionDays(
            Verbose: 2, Debug: 3, Information: 45,
            Warning: LogRetentionDays.Default.Warning,
            Error: LogRetentionDays.Default.Error,
            Fatal: LogRetentionDays.Default.Fatal));
    }
}

public class LogRetentionDaysTests
{
    [Fact]
    public void SanitizedOrDefault_replaces_each_out_of_range_member_with_the_default_for_that_level_only()
    {
        var raw = new LogRetentionDays(Verbose: 0, Debug: 5, Information: -5, Warning: 90, Error: 4000, Fatal: 1);

        var sanitized = raw.SanitizedOrDefault();

        sanitized.Should().Be(new LogRetentionDays(
            Verbose: LogRetentionDays.Default.Verbose,
            Debug: 5,
            Information: LogRetentionDays.Default.Information,
            Warning: 90,
            Error: LogRetentionDays.Default.Error,
            Fatal: 1));
    }

    [Fact]
    public void SanitizedOrDefault_leaves_an_already_valid_row_untouched()
    {
        var valid = new LogRetentionDays(2, 3, 45, 100, 120, 365);

        valid.SanitizedOrDefault().Should().Be(valid);
    }
}
