using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
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

    [Theory]
    [InlineData(LogSeverity.Verbose)]
    [InlineData(LogSeverity.Debug)]
    public async Task Verbose_and_debug_older_than_seven_days_are_pruned_but_not_seven_days_exactly(LogSeverity level)
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        db.AppLogs.AddRange(
            Row(1, level, Now.AddDays(-7).AddSeconds(1)), // just inside 7 days: kept
            Row(2, level, Now.AddDays(-7).AddSeconds(-1))); // just past 7 days: pruned
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var deleted = await new EfLogRetention(db).PruneAsync(Now, TestContext.Current.CancellationToken);

        deleted.Should().Be(1);
        (await RemainingIdsAsync(db)).Should().Equal(1);
    }

    [Fact]
    public async Task Information_older_than_ninety_days_is_pruned_but_not_ninety_days_exactly()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        db.AppLogs.AddRange(
            Row(1, LogSeverity.Information, Now.AddDays(-90).AddSeconds(1)),
            Row(2, LogSeverity.Information, Now.AddDays(-90).AddSeconds(-1)),
            Row(3, LogSeverity.Information, Now.AddDays(-8))); // inside 7 days boundary irrelevant to Information

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var deleted = await new EfLogRetention(db).PruneAsync(Now, TestContext.Current.CancellationToken);

        deleted.Should().Be(1);
        (await RemainingIdsAsync(db)).Should().Equal(1, 3);
    }

    [Theory]
    [InlineData(LogSeverity.Warning)]
    [InlineData(LogSeverity.Error)]
    [InlineData(LogSeverity.Fatal)]
    public async Task Warning_and_above_older_than_730_days_are_pruned_but_not_730_days_exactly(LogSeverity level)
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        db.AppLogs.AddRange(
            Row(1, level, Now.AddDays(-730).AddSeconds(1)),
            Row(2, level, Now.AddDays(-730).AddSeconds(-1)));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var deleted = await new EfLogRetention(db).PruneAsync(Now, TestContext.Current.CancellationToken);

        deleted.Should().Be(1);
        (await RemainingIdsAsync(db)).Should().Equal(1);
    }

    [Fact]
    public async Task An_information_row_seven_days_old_is_not_pruned_by_the_verbose_debug_band()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        db.AppLogs.Add(Row(1, LogSeverity.Information, Now.AddDays(-8)));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var deleted = await new EfLogRetention(db).PruneAsync(Now, TestContext.Current.CancellationToken);

        deleted.Should().Be(0);
        (await RemainingIdsAsync(db)).Should().Equal(1);
    }
}
