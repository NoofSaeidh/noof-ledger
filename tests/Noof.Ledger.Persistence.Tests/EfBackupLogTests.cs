using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Backup;
using Noof.Ledger.Persistence.Backup;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfBackupLogTests(PostgresFixture fixture)
{
    static readonly DateTimeOffset T0 = new(2026, 9, 24, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_run_that_never_happened_reports_no_success_and_no_failure()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var log = new EfBackupLog(db);

        var status = await log.StatusAsync(TestContext.Current.CancellationToken);

        status.Should().Be(new BackupStatus(null, false, null));
    }

    [Fact]
    public async Task A_successful_run_is_the_last_success_and_not_a_failure()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var log = new EfBackupLog(db);

        await log.RecordAsync(new BackupRunRecord(T0, T0.AddMinutes(1), true, "noof_ledger-20260924-030000.dump", 4096, null),
            TestContext.Current.CancellationToken);

        var status = await log.StatusAsync(TestContext.Current.CancellationToken);
        status.Should().Be(new BackupStatus(T0.AddMinutes(1), false, null));
    }

    [Fact]
    public async Task A_failure_after_an_earlier_success_keeps_the_success_but_reports_the_failure()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var log = new EfBackupLog(db);

        await log.RecordAsync(new BackupRunRecord(T0, T0.AddMinutes(1), true, "noof_ledger-20260924-030000.dump", 4096, null),
            TestContext.Current.CancellationToken);
        await log.RecordAsync(new BackupRunRecord(T0.AddDays(1), T0.AddDays(1).AddSeconds(5), false, null, null, "pg_dump exited with code 1"),
            TestContext.Current.CancellationToken);

        var status = await log.StatusAsync(TestContext.Current.CancellationToken);
        status.LastSuccessAt.Should().Be(T0.AddMinutes(1));
        status.LastRunFailed.Should().BeTrue();
        status.LastError.Should().Be("pg_dump exited with code 1");
    }
}
