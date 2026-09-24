using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Noof.Ledger.Application.Backup;
using Noof.Ledger.Host.Workers;

namespace Noof.Ledger.Host.Tests;

public class BackupWorkerTests : IDisposable
{
    readonly string backupDirectory = Path.Combine(Path.GetTempPath(), $"noof-backup-worker-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(backupDirectory))
            Directory.Delete(backupDirectory, recursive: true);
    }

    BackupWorkerOptions Options(int keepCount = 14, TimeSpan? retryInterval = null) => new()
    {
        BackupDirectory = backupDirectory,
        Interval = TimeSpan.FromHours(24),
        KeepCount = keepCount,
        RetryInterval = retryInterval ?? TimeSpan.FromHours(1),
    };

    static IServiceScopeFactory ScopeFactoryFor(IBackupLog backupLog, IDatabaseDumper dumper)
    {
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IBackupLog)).Returns(backupLog);
        provider.GetService(typeof(IDatabaseDumper)).Returns(dumper);

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);

        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);
        return factory;
    }

    static IBackupLog LogWithStatus(BackupStatus status)
    {
        var log = Substitute.For<IBackupLog>();
        log.StatusAsync(Arg.Any<CancellationToken>()).Returns(status);
        return log;
    }

    static BackupWorker CreateWorker(IServiceScopeFactory scopeFactory, FakeTimeProvider time, BackupWorkerOptions options) =>
        new(scopeFactory, time, options, NullLogger<BackupWorker>.Instance);

    [Fact]
    public async Task A_backup_that_never_ran_before_is_taken_immediately()
    {
        var now = new DateTimeOffset(2026, 9, 24, 3, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var log = LogWithStatus(new BackupStatus(null, false, null));
        var dumper = Substitute.For<IDatabaseDumper>();
        dumper.DumpAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => WriteFakeDumpAsync(ci.Arg<string>(), new DumpResult(true, null)));
        var worker = CreateWorker(ScopeFactoryFor(log, dumper), time, Options());

        var result = await worker.RunTickAsync(TestContext.Current.CancellationToken);

        result.Should().Be(BackupTickResult.BackedUp);
        var files = Directory.GetFiles(backupDirectory, "noof_ledger-*.dump");
        files.Should().ContainSingle();
        Path.GetFileName(files[0]).Should().Be("noof_ledger-20260924-030000.dump");
        await log.Received(1).RecordAsync(
            Arg.Is<BackupRunRecord>(r => r.Succeeded && r.FileName == "noof_ledger-20260924-030000.dump" && r.SizeBytes > 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_backup_less_than_a_day_old_is_skipped_without_calling_the_dumper()
    {
        var now = new DateTimeOffset(2026, 9, 24, 3, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var log = LogWithStatus(new BackupStatus(now.AddHours(-2), false, null));
        var dumper = Substitute.For<IDatabaseDumper>();
        var worker = CreateWorker(ScopeFactoryFor(log, dumper), time, Options());

        var result = await worker.RunTickAsync(TestContext.Current.CancellationToken);

        result.Should().Be(BackupTickResult.Skipped);
        await dumper.DidNotReceive().DumpAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await log.DidNotReceive().RecordAsync(Arg.Any<BackupRunRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_backup_more_than_a_day_stale_is_taken_again()
    {
        var now = new DateTimeOffset(2026, 9, 24, 3, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var log = LogWithStatus(new BackupStatus(now.AddHours(-25), false, null));
        var dumper = Substitute.For<IDatabaseDumper>();
        dumper.DumpAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => WriteFakeDumpAsync(ci.Arg<string>(), new DumpResult(true, null)));
        var worker = CreateWorker(ScopeFactoryFor(log, dumper), time, Options());

        var result = await worker.RunTickAsync(TestContext.Current.CancellationToken);

        result.Should().Be(BackupTickResult.BackedUp);
    }

    [Fact]
    public async Task A_failed_dump_is_recorded_leaves_no_temp_file_and_does_not_throw()
    {
        var now = new DateTimeOffset(2026, 9, 24, 3, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var log = LogWithStatus(new BackupStatus(null, false, null));
        var dumper = Substitute.For<IDatabaseDumper>();
        dumper.DumpAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new DumpResult(false, "pg_dump exited with code 1"));
        var worker = CreateWorker(ScopeFactoryFor(log, dumper), time, Options());

        var result = await worker.RunTickAsync(TestContext.Current.CancellationToken);

        result.Should().Be(BackupTickResult.Failed);
        Directory.Exists(backupDirectory).Should().BeTrue();
        Directory.GetFiles(backupDirectory).Should().BeEmpty("a failed dump must leave no half-written file behind");
        await log.Received(1).RecordAsync(
            Arg.Is<BackupRunRecord>(r => !r.Succeeded && r.FileName == null && r.Error == "pg_dump exited with code 1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_dumper_that_throws_is_recorded_as_a_failure_not_a_crash()
    {
        var now = new DateTimeOffset(2026, 9, 24, 3, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var log = LogWithStatus(new BackupStatus(null, false, null));
        var dumper = Substitute.For<IDatabaseDumper>();
        dumper.DumpAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("disk full"));
        var worker = CreateWorker(ScopeFactoryFor(log, dumper), time, Options());

        var result = await worker.RunTickAsync(TestContext.Current.CancellationToken);

        result.Should().Be(BackupTickResult.Failed);
        await log.Received(1).RecordAsync(Arg.Is<BackupRunRecord>(r => !r.Succeeded && r.Error == "disk full"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_scope_that_cannot_be_created_fails_the_tick_without_throwing()
    {
        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Throws(new InvalidOperationException("no scope"));
        var worker = CreateWorker(factory, new FakeTimeProvider(DateTimeOffset.UtcNow), Options());

        var result = await worker.RunTickAsync(TestContext.Current.CancellationToken);

        result.Should().Be(BackupTickResult.Failed);
    }

    [Fact]
    public async Task A_failed_tick_is_retried_after_the_retry_interval_not_a_day_later()
    {
        var now = new DateTimeOffset(2026, 9, 24, 3, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var log = Substitute.For<IBackupLog>();
        log.StatusAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("database unreachable"));
        var dumper = Substitute.For<IDatabaseDumper>();
        var worker = CreateWorker(ScopeFactoryFor(log, dumper), time, Options(retryInterval: TimeSpan.FromHours(1)));

        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
            await log.Received(1).StatusAsync(Arg.Any<CancellationToken>());

            time.Advance(TimeSpan.FromHours(1));
            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

            await log.Received(2).StatusAsync(Arg.Any<CancellationToken>());
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_successful_backup_prunes_down_to_the_configured_count()
    {
        Directory.CreateDirectory(backupDirectory);
        for (var day = 1; day <= 3; day++)
            File.WriteAllText(Path.Combine(backupDirectory, $"noof_ledger-202609{day:D2}-030000.dump"), "old");

        var now = new DateTimeOffset(2026, 9, 24, 3, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var log = LogWithStatus(new BackupStatus(null, false, null));
        var dumper = Substitute.For<IDatabaseDumper>();
        dumper.DumpAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => WriteFakeDumpAsync(ci.Arg<string>(), new DumpResult(true, null)));
        var worker = CreateWorker(ScopeFactoryFor(log, dumper), time, Options(keepCount: 2));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        var remaining = Directory.GetFiles(backupDirectory, "noof_ledger-*.dump").Select(Path.GetFileName).ToArray();
        remaining.Should().BeEquivalentTo(["noof_ledger-20260903-030000.dump", "noof_ledger-20260924-030000.dump"]);
    }

    static Task<DumpResult> WriteFakeDumpAsync(string targetPath, DumpResult result)
    {
        if (result.Succeeded)
            File.WriteAllText(targetPath, "fake dump contents");
        return Task.FromResult(result);
    }
}
