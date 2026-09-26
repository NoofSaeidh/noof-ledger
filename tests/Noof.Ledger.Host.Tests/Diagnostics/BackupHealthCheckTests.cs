using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Noof.Ledger.Application.Backup;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Host.Diagnostics;

namespace Noof.Ledger.Host.Tests.Diagnostics;

public class BackupHealthCheckTests
{
    static readonly DateTimeOffset T0 = new(2026, 9, 25, 3, 0, 0, TimeSpan.Zero);

    static IDatabaseGate ReadyGate()
    {
        var gate = Substitute.For<IDatabaseGate>();
        gate.State.Returns(DatabaseState.Ready);
        return gate;
    }

    static IBackupLog LogWith(BackupStatus status)
    {
        var log = Substitute.For<IBackupLog>();
        log.StatusAsync(Arg.Any<CancellationToken>()).Returns(status);
        return log;
    }

    [Fact]
    public void Reports_its_name_order_and_log_category()
    {
        var check = new BackupHealthCheck(ReadyGate(), LogWith(new BackupStatus(T0, false, null)), new FakeTimeProvider(T0));

        check.Name.Should().Be("Backup");
        check.Order.Should().Be(50);
        check.LogCategory.Should().Be("Noof.Ledger.Host.Workers.BackupWorker");
    }

    [Fact]
    public async Task Reports_a_warning_when_the_gate_is_not_ready()
    {
        var gate = Substitute.For<IDatabaseGate>();
        gate.State.Returns(DatabaseState.Waiting);
        var check = new BackupHealthCheck(gate, LogWith(new BackupStatus(T0, false, null)), new FakeTimeProvider(T0));

        var result = await check.CheckAsync(TestContext.Current.CancellationToken);

        result.Level.Should().Be(HealthLevel.Warning);
        result.Summary.Should().Be("Waiting for the database");
    }

    [Fact]
    public async Task A_success_under_26_hours_old_is_ok()
    {
        var check = new BackupHealthCheck(ReadyGate(), LogWith(new BackupStatus(T0.AddHours(-25), false, null)), new FakeTimeProvider(T0));

        var result = await check.CheckAsync(TestContext.Current.CancellationToken);

        result.Level.Should().Be(HealthLevel.Ok);
    }

    [Fact]
    public async Task A_success_26_hours_or_older_is_a_warning()
    {
        var check = new BackupHealthCheck(ReadyGate(), LogWith(new BackupStatus(T0.AddHours(-26), false, null)), new FakeTimeProvider(T0));

        var result = await check.CheckAsync(TestContext.Current.CancellationToken);

        result.Level.Should().Be(HealthLevel.Warning);
    }

    [Fact]
    public async Task A_failed_last_run_is_a_warning_even_with_a_recent_success()
    {
        var check = new BackupHealthCheck(ReadyGate(), LogWith(new BackupStatus(T0.AddHours(-1), true, "disk full")), new FakeTimeProvider(T0));

        var result = await check.CheckAsync(TestContext.Current.CancellationToken);

        result.Level.Should().Be(HealthLevel.Warning);
    }

    [Fact]
    public async Task No_backup_ever_succeeded_is_a_warning()
    {
        var check = new BackupHealthCheck(ReadyGate(), LogWith(new BackupStatus(null, false, null)), new FakeTimeProvider(T0));

        var result = await check.CheckAsync(TestContext.Current.CancellationToken);

        result.Level.Should().Be(HealthLevel.Warning);
    }
}
