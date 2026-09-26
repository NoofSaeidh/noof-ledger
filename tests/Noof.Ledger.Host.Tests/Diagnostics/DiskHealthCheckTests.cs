using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Host.Diagnostics;
using Noof.Ledger.Host.Workers;

namespace Noof.Ledger.Host.Tests.Diagnostics;

public class DiskHealthCheckTests
{
    const long Gb = 1024L * 1024 * 1024;

    static IDatabaseGate ReadyGate()
    {
        var gate = Substitute.For<IDatabaseGate>();
        gate.State.Returns(DatabaseState.Ready);
        return gate;
    }

    static IConfiguration ConfigWithLogDirectory(string directory) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Logging:File:Directory"] = directory })
            .Build();

    static DiskHealthCheck Check(long logFreeBytes, long backupFreeBytes)
    {
        var freeSpace = Substitute.For<IFreeSpaceProvider>();
        freeSpace.GetAvailableFreeBytes("C:\\logs").Returns(logFreeBytes);
        freeSpace.GetAvailableFreeBytes("C:\\backups").Returns(backupFreeBytes);
        return new DiskHealthCheck(ReadyGate(), freeSpace, ConfigWithLogDirectory("C:\\logs"),
            new BackupWorkerOptions { BackupDirectory = "C:\\backups" });
    }

    [Fact]
    public async Task Plenty_of_space_on_both_drives_is_healthy()
    {
        var check = Check(logFreeBytes: 10 * Gb, backupFreeBytes: 10 * Gb);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task The_worse_of_the_two_drives_decides_the_result()
    {
        var check = Check(logFreeBytes: 10 * Gb, backupFreeBytes: 3 * Gb);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task Under_one_gigabyte_free_is_unhealthy()
    {
        var check = Check(logFreeBytes: 10 * Gb, backupFreeBytes: (long)(0.5 * Gb));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task Reports_degraded_when_the_gate_is_not_ready()
    {
        var freeSpace = Substitute.For<IFreeSpaceProvider>();
        var gate = Substitute.For<IDatabaseGate>();
        gate.State.Returns(DatabaseState.Waiting);
        var check = new DiskHealthCheck(gate, freeSpace, ConfigWithLogDirectory("C:\\logs"),
            new BackupWorkerOptions { BackupDirectory = "C:\\backups" });

        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Be("Waiting for the database");
    }

    [Fact]
    public async Task A_free_space_read_that_never_returns_is_abandoned_when_cancelled()
    {
        var gate = ReadyGate();
        using var block = new ManualResetEventSlim(initialState: false);
        var freeSpace = Substitute.For<IFreeSpaceProvider>();
        freeSpace.GetAvailableFreeBytes(Arg.Any<string>()).Returns(_ =>
        {
            try
            {
                block.Wait();
                return 10 * Gb;
            }
            finally
            {
                block.Set();
            }
        });
        var check = new DiskHealthCheck(gate, freeSpace, ConfigWithLogDirectory("C:\\logs"),
            new BackupWorkerOptions { BackupDirectory = "C:\\backups" });
        using var cts = new CancellationTokenSource();

        var task = check.CheckHealthAsync(new HealthCheckContext(), cts.Token);
        cts.Cancel();

        var act = async () => await task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<OperationCanceledException>();

        block.Set();
    }
}
