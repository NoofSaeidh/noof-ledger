using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Persistence.Diagnostics;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfLogRetentionTests(PostgresFixture fixture)
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");

    static AppLogEntry Row(int n, LogSeverity level, DateTimeOffset loggedAt) => new()
    {
        Id = n,
        LoggedAt = loggedAt,
        Level = level,
        Source = "Test",
        Message = "m",
        Template = "m",
    };

    static async Task<List<long>> RemainingIdsAsync(LedgerDbContext db) =>
        await db.AppLogs.AsNoTracking().OrderBy(e => e.Id).Select(e => e.Id).ToListAsync(TestContext.Current.CancellationToken);

    static EfLogRetention RetentionFor(LedgerDbContext db, LogRetentionOptions? options = null) =>
        new(db, new FakeTimeProvider(Now), options ?? new LogRetentionOptions());

    [Theory]
    [InlineData(LogSeverity.Verbose)]
    [InlineData(LogSeverity.Debug)]
    public async Task Verbose_and_debug_older_than_the_default_one_day_window_are_pruned_but_not_exactly_at_the_boundary(LogSeverity level)
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        db.AppLogs.AddRange(
            Row(1, level, Now.AddDays(-1).AddSeconds(1)), // just inside 1 day: kept
            Row(2, level, Now.AddDays(-1).AddSeconds(-1))); // just past 1 day: pruned
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var deleted = await RetentionFor(db).PruneAsync(TestContext.Current.CancellationToken);

        deleted.Should().Be(1);
        (await RemainingIdsAsync(db)).Should().Equal(1);
    }

    [Theory]
    [InlineData(LogSeverity.Information, 90)]
    [InlineData(LogSeverity.Warning, 730)]
    [InlineData(LogSeverity.Error, 730)]
    [InlineData(LogSeverity.Fatal, 730)]
    public async Task Information_and_above_older_than_their_default_window_are_pruned_but_not_exactly_at_the_boundary(LogSeverity level, int windowDays)
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        db.AppLogs.AddRange(
            Row(1, level, Now.AddDays(-windowDays).AddSeconds(1)),
            Row(2, level, Now.AddDays(-windowDays).AddSeconds(-1)));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var deleted = await RetentionFor(db).PruneAsync(TestContext.Current.CancellationToken);

        deleted.Should().Be(1);
        (await RemainingIdsAsync(db)).Should().Equal(1);
    }

    [Fact]
    public async Task Each_level_is_pruned_by_its_own_window_not_a_shared_one()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        // Two days old: past Debug's default one-day window, well inside Information's default
        // ninety-day window - proves each level's cutoff is computed and applied independently, not
        // one shared cutoff for the whole table.
        db.AppLogs.AddRange(
            Row(1, LogSeverity.Debug, Now.AddDays(-2)),
            Row(2, LogSeverity.Information, Now.AddDays(-2)));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var deleted = await RetentionFor(db).PruneAsync(TestContext.Current.CancellationToken);

        deleted.Should().Be(1);
        (await RemainingIdsAsync(db)).Should().Equal(2);
    }

    [Fact]
    public async Task A_configured_window_overrides_the_default_for_that_level_only()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        db.AppLogs.AddRange(
            Row(1, LogSeverity.Warning, Now.AddDays(-10)),
            Row(2, LogSeverity.Error, Now.AddDays(-10)));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var options = new LogRetentionOptions { Days = new() { Warning = 5 } };

        var deleted = await RetentionFor(db, options).PruneAsync(TestContext.Current.CancellationToken);

        deleted.Should().Be(1);
        (await RemainingIdsAsync(db)).Should().Equal(2);
    }
}
