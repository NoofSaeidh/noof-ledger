using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Persistence.Settings;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfAutoSummaryMarkerTests(PostgresFixture fixture)
{
    static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static EfAutoSummaryMarker MarkerFor(LedgerDbContext db) => new(db, new FakeTimeProvider(Now));

    [Fact]
    public async Task With_nothing_sent_yet_there_is_no_marker()
    {
        await using var db = await fixture.CreateMigratedContextAsync();

        (await MarkerFor(db).GetAsync(Ct)).Should().BeNull();
    }

    [Fact]
    public async Task A_month_set_reads_back_as_its_first_day_and_is_stored_as_year_and_month()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var marker = MarkerFor(db);

        await marker.SetAsync(new DateOnly(2026, 9, 1), Ct);

        (await marker.GetAsync(Ct)).Should().Be(new DateOnly(2026, 9, 1));
        (await db.AppSettings.AsNoTracking().SingleAsync(row => row.Key == "summary.last-auto-month", Ct))
            .Value.Should().Be("2026-09");
    }

    [Fact]
    public async Task A_second_month_replaces_the_first()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var marker = MarkerFor(db);

        await marker.SetAsync(new DateOnly(2026, 8, 1), Ct);
        await marker.SetAsync(new DateOnly(2026, 9, 1), Ct);

        (await marker.GetAsync(Ct)).Should().Be(new DateOnly(2026, 9, 1));
    }

    [Theory]
    [InlineData("2026-13")]
    [InlineData("September")]
    [InlineData("")]
    public async Task A_stored_value_that_is_not_a_month_reads_as_no_marker(string stored)
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO app_setting (key, value, updated_at) VALUES ('summary.last-auto-month', {stored}, {Now})", Ct);

        (await MarkerFor(db).GetAsync(Ct)).Should().BeNull();
    }

    static readonly DateOnly September = new(2026, 9, 1);

    [Fact]
    public async Task With_no_month_waiting_there_is_no_waiting_stamp()
    {
        await using var db = await fixture.CreateMigratedContextAsync();

        (await MarkerFor(db).GetWaitingSinceAsync(September, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task A_waiting_stamp_reads_back_for_its_month_only_and_is_stored_in_UTC()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var marker = MarkerFor(db);
        var since = new DateTimeOffset(2026, 10, 1, 11, 5, 0, TimeSpan.FromHours(2));

        await marker.SetWaitingSinceAsync(September, since, Ct);

        (await marker.GetWaitingSinceAsync(September, Ct)).Should().Be(since);
        (await marker.GetWaitingSinceAsync(new DateOnly(2026, 8, 1), Ct)).Should().BeNull(
            "the stamp belongs to one month; another month has not started waiting");
        (await db.AppSettings.AsNoTracking().SingleAsync(row => row.Key == "summary.auto-waiting-since", Ct))
            .Value.Should().Be("2026-09|2026-10-01T09:05:00.0000000+00:00");
    }

    [Fact]
    public async Task A_new_months_stamp_replaces_the_last_ones_and_leaves_the_sent_marker_alone()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var marker = MarkerFor(db);
        await marker.SetAsync(new DateOnly(2026, 8, 1), Ct);
        await marker.SetWaitingSinceAsync(September, new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero), Ct);

        var october = new DateOnly(2026, 10, 1);
        var since = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
        await marker.SetWaitingSinceAsync(october, since, Ct);

        (await marker.GetWaitingSinceAsync(September, Ct)).Should().BeNull();
        (await marker.GetWaitingSinceAsync(october, Ct)).Should().Be(since);
        (await marker.GetAsync(Ct)).Should().Be(new DateOnly(2026, 8, 1));
    }

    [Theory]
    [InlineData("2026-09")]
    [InlineData("2026-09|yesterday")]
    [InlineData("2026-09|2026-10-01T09:05:00Z|extra")]
    [InlineData("")]
    public async Task A_waiting_stamp_that_does_not_parse_reads_as_none(string stored)
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO app_setting (key, value, updated_at) VALUES ('summary.auto-waiting-since', {stored}, {Now})", Ct);

        (await MarkerFor(db).GetWaitingSinceAsync(September, Ct)).Should().BeNull();
    }
}
