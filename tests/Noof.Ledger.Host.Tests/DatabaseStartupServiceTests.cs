using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Host.Startup;
using Npgsql;

namespace Noof.Ledger.Host.Tests;

public class DatabaseStartupServiceTests
{
    static DatabaseStartupService CreateService(
        IDatabaseStartupProbe probe, DatabaseGate gate, FakeTimeProvider time, bool migrateOnStartup = true)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new("Database:MigrateOnStartup", migrateOnStartup.ToString())])
            .Build();

        return new DatabaseStartupService(probe, gate, configuration, time, NullLogger<DatabaseStartupService>.Instance);
    }

    [Fact]
    public async Task A_connection_that_opens_and_a_migration_that_succeeds_makes_the_gate_ready()
    {
        var probe = Substitute.For<IDatabaseStartupProbe>();
        probe.OpenConnectionAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        probe.MigrateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var gate = new DatabaseGate();

        var result = await CreateService(probe, gate, new FakeTimeProvider())
            .RunAttemptAsync(TestContext.Current.CancellationToken);

        result.Should().Be(DatabaseStartupResult.Ready);
        gate.State.Should().Be(DatabaseState.Ready);
    }

    [Fact]
    public async Task MigrateOnStartup_false_skips_migration_and_still_reaches_ready()
    {
        var probe = Substitute.For<IDatabaseStartupProbe>();
        probe.OpenConnectionAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var gate = new DatabaseGate();

        var result = await CreateService(probe, gate, new FakeTimeProvider(), migrateOnStartup: false)
            .RunAttemptAsync(TestContext.Current.CancellationToken);

        result.Should().Be(DatabaseStartupResult.Ready);
        gate.State.Should().Be(DatabaseState.Ready);
        await probe.DidNotReceive().MigrateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Gate_reports_Migrating_while_the_migration_runs()
    {
        var probe = Substitute.For<IDatabaseStartupProbe>();
        probe.OpenConnectionAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var gate = new DatabaseGate();
        probe.MigrateAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            gate.State.Should().Be(DatabaseState.Migrating);
            return Task.CompletedTask;
        });

        await CreateService(probe, gate, new FakeTimeProvider()).RunAttemptAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_connection_that_never_opens_is_classified_as_Waiting_not_Failed()
    {
        var probe = Substitute.For<IDatabaseStartupProbe>();
        probe.OpenConnectionAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new NpgsqlException("Connection refused"));
        var gate = new DatabaseGate();

        var result = await CreateService(probe, gate, new FakeTimeProvider())
            .RunAttemptAsync(TestContext.Current.CancellationToken);

        result.Should().Be(DatabaseStartupResult.WaitingRetry);
        gate.State.Should().Be(DatabaseState.Waiting);
        gate.Detail.Should().Be("Connection refused");
    }

    [Fact]
    public async Task A_socket_level_failure_is_classified_as_Waiting()
    {
        var probe = Substitute.For<IDatabaseStartupProbe>();
        probe.OpenConnectionAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new System.Net.Sockets.SocketException());
        var gate = new DatabaseGate();

        var result = await CreateService(probe, gate, new FakeTimeProvider())
            .RunAttemptAsync(TestContext.Current.CancellationToken);

        result.Should().Be(DatabaseStartupResult.WaitingRetry);
        gate.State.Should().Be(DatabaseState.Waiting);
    }

    [Fact]
    public async Task A_migration_failure_is_classified_as_Failed_not_Waiting()
    {
        var probe = Substitute.For<IDatabaseStartupProbe>();
        probe.OpenConnectionAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        probe.MigrateAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new PostgresException("relation already exists", "ERROR", "ERROR", "42P07"));
        var gate = new DatabaseGate();

        var result = await CreateService(probe, gate, new FakeTimeProvider())
            .RunAttemptAsync(TestContext.Current.CancellationToken);

        result.Should().Be(DatabaseStartupResult.FailedRetry);
        gate.State.Should().Be(DatabaseState.Failed);
        gate.Detail.Should().Be("relation already exists");
    }

    [Fact]
    public async Task An_unmodeled_exception_from_the_migration_step_is_classified_as_Failed()
    {
        var probe = Substitute.For<IDatabaseStartupProbe>();
        probe.OpenConnectionAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        probe.MigrateAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));
        var gate = new DatabaseGate();

        var result = await CreateService(probe, gate, new FakeTimeProvider())
            .RunAttemptAsync(TestContext.Current.CancellationToken);

        result.Should().Be(DatabaseStartupResult.FailedRetry);
        gate.State.Should().Be(DatabaseState.Failed);
    }

    [Fact]
    public async Task The_backoff_sequence_is_2_4_8_16_30_then_30_forever()
    {
        var probe = Substitute.For<IDatabaseStartupProbe>();
        probe.OpenConnectionAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new NpgsqlException("Connection refused"));
        var gate = new DatabaseGate();
        var time = new FakeTimeProvider();
        var service = CreateService(probe, gate, time);

        TimeSpan[] expected =
        [
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30),
        ];

        List<TimeSpan> observed = [];
        foreach (var _ in expected)
        {
            await service.RunAttemptAsync(TestContext.Current.CancellationToken);

            // RunAttemptAsync itself never delays (ExecuteAsync does, between attempts) - this test
            // exercises the pure backoff function the loop consults, driven the same way
            // CategorizationWorkerTests drives RunTickAsync in a loop.
            observed.Add(service.NextDelay());
            time.Advance(observed[^1]);
        }

        observed.Should().Equal(expected);
    }

    [Fact]
    public async Task Logs_the_first_ten_connection_failures_then_only_every_tenth()
    {
        var probe = Substitute.For<IDatabaseStartupProbe>();
        probe.OpenConnectionAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new NpgsqlException("Connection refused"));
        var gate = new DatabaseGate();
        var logger = new ListLogger<DatabaseStartupService>();
        var service = new DatabaseStartupService(
            probe, gate, new ConfigurationBuilder().Build(), new FakeTimeProvider(), logger);

        for (var attempt = 1; attempt <= 21; attempt++)
            await service.RunAttemptAsync(TestContext.Current.CancellationToken);

        // Attempts 1-10, then 20: 11 log lines out of 21 failures.
        logger.Entries.Should().HaveCount(11);
    }
}
