using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Host.Workers;
using Noof.Ledger.TestKit;

namespace Noof.Ledger.Host.Tests;

public class LogRetentionWorkerTests
{
    static IServiceScopeFactory ScopeFactoryFor(ILogRetention retention)
    {
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(ILogRetention)).Returns(retention);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);
        return factory;
    }

    static IDatabaseGate ReadyGate()
    {
        var gate = Substitute.For<IDatabaseGate>();
        gate.WaitUntilReadyAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return gate;
    }

    [Fact]
    public async Task The_worker_waits_for_the_gate_before_its_first_prune()
    {
        var retention = Substitute.For<ILogRetention>();
        retention.PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(0);
        var gate = Substitute.For<IDatabaseGate>();
        var gateReady = new TaskCompletionSource();
        gate.WaitUntilReadyAsync(Arg.Any<CancellationToken>()).Returns(gateReady.Task);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero));
        var worker = new LogRetentionWorker(ScopeFactoryFor(retention), gate, time, NullLogger<LogRetentionWorker>.Instance);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
            await retention.DidNotReceive().PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

            gateReady.SetResult();
            time.Advance(TimeSpan.FromSeconds(60));
            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
            await retention.Received(1).PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task The_first_prune_runs_sixty_seconds_after_ready_not_immediately()
    {
        var retention = Substitute.For<ILogRetention>();
        retention.PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(0);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero));
        var worker = new LogRetentionWorker(ScopeFactoryFor(retention), ReadyGate(), time, NullLogger<LogRetentionWorker>.Instance);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
            await retention.DidNotReceive().PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

            time.Advance(TimeSpan.FromSeconds(59));
            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
            await retention.DidNotReceive().PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
            await retention.Received(1).PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Subsequent_prunes_run_every_six_hours()
    {
        var retention = Substitute.For<ILogRetention>();
        retention.PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(0);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero));
        var worker = new LogRetentionWorker(ScopeFactoryFor(retention), ReadyGate(), time, NullLogger<LogRetentionWorker>.Instance);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromSeconds(60));
            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
            await retention.Received(1).PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

            time.Advance(TimeSpan.FromHours(6) - TimeSpan.FromSeconds(1));
            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
            await retention.Received(1).PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
            await retention.Received(2).PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Log_messages_carry_stable_EventIds()
    {
        var retention = Substitute.For<ILogRetention>();
        retention.PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(_ => 0, _ => throw new InvalidOperationException("database unreachable"));
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero));
        var logger = new CapturingLogger<LogRetentionWorker>();
        var worker = new LogRetentionWorker(ScopeFactoryFor(retention), ReadyGate(), time, logger);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromSeconds(60));
            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

            time.Advance(TimeSpan.FromHours(6));
            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }

        // M-7 (Phase 5 final review): explicit, pinned ids in the 52xx block - the same convention
        // every other [LoggerMessage] on the branch follows (DatabaseStartupService's 5101/5102,
        // BackupWorker's 1101/1102, ...) - not whatever the source generator assigns implicitly by
        // declaration order, which drifts the moment a method is added, removed or reordered.
        logger.Entries.Select(entry => entry.EventId.Id).Should().BeEquivalentTo([5201, 5202]);
    }

    [Fact]
    public async Task A_prune_that_throws_is_logged_and_never_crashes_the_worker()
    {
        var retention = Substitute.For<ILogRetention>();
        retention.PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database unreachable"));
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero));
        var worker = new LogRetentionWorker(ScopeFactoryFor(retention), ReadyGate(), time, NullLogger<LogRetentionWorker>.Instance);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromSeconds(60));
            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
            await retention.Received(1).PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

            // Still alive: the failed tick did not stop the loop, so the next scheduled tick still fires.
            time.Advance(TimeSpan.FromHours(6));
            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
            await retention.Received(2).PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}
