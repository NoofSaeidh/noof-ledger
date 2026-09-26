using AwesomeAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Host.Diagnostics;

namespace Noof.Ledger.Host.Tests.Diagnostics;

public class SystemHealthTests
{
    static readonly DateTimeOffset T0 = new(2026, 9, 25, 3, 0, 0, TimeSpan.Zero);

    static HealthReport ReportOf(params (string Name, HealthStatus Status, string Description)[] entries) =>
        new(entries.ToDictionary(e => e.Name, e => new HealthReportEntry(
            data: new Dictionary<string, object>(),
            description: e.Description,
            duration: TimeSpan.Zero,
            exception: null,
            status: e.Status,
            tags: [])),
            entries.Aggregate(TimeSpan.Zero, (acc, _) => acc));

    static HealthCheckService ServiceReturning(HealthReport report)
    {
        var service = Substitute.For<HealthCheckService>();
        service.CheckHealthAsync(Arg.Any<CancellationToken>()).Returns(report);
        return service;
    }

    [Fact]
    public async Task Items_come_back_in_HealthCheckNames_Ordered_order_regardless_of_report_order()
    {
        var report = ReportOf(
            (HealthCheckNames.LogSink, HealthStatus.Healthy, "ok"),
            (HealthCheckNames.Database, HealthStatus.Healthy, "ready"),
            (HealthCheckNames.Migrations, HealthStatus.Healthy, "up to date"),
            (HealthCheckNames.Telegram, HealthStatus.Healthy, "connected"),
            (HealthCheckNames.AiKeys, HealthStatus.Healthy, "configured"),
            (HealthCheckNames.Backup, HealthStatus.Healthy, "fresh"),
            (HealthCheckNames.Disk, HealthStatus.Healthy, "10.0 GB free"));
        var health = new SystemHealth(ServiceReturning(report), new FakeTimeProvider(T0));

        var result = await health.GetAsync(fresh: true, TestContext.Current.CancellationToken);

        result.Items.Select(i => i.Name).Should().BeEquivalentTo(HealthCheckNames.Ordered, options => options.WithStrictOrdering());
    }

    [Theory]
    [InlineData(HealthStatus.Healthy, HealthLevel.Ok)]
    [InlineData(HealthStatus.Degraded, HealthLevel.Warning)]
    [InlineData(HealthStatus.Unhealthy, HealthLevel.Failing)]
    public async Task Maps_HealthStatus_to_HealthLevel(HealthStatus status, HealthLevel expected)
    {
        var report = ReportOf((HealthCheckNames.Database, status, "x"));
        var health = new SystemHealth(ServiceReturning(report), new FakeTimeProvider(T0));

        var result = await health.GetAsync(fresh: true, TestContext.Current.CancellationToken);

        result.Items.Single(i => i.Name == HealthCheckNames.Database).Level.Should().Be(expected);
    }

    [Fact]
    public async Task Overall_is_the_worst_of_all_items()
    {
        var report = ReportOf(
            (HealthCheckNames.Database, HealthStatus.Healthy, "ready"),
            (HealthCheckNames.Migrations, HealthStatus.Healthy, "up to date"),
            (HealthCheckNames.Telegram, HealthStatus.Healthy, "connected"),
            (HealthCheckNames.AiKeys, HealthStatus.Healthy, "configured"),
            (HealthCheckNames.Backup, HealthStatus.Degraded, "stale"),
            (HealthCheckNames.Disk, HealthStatus.Healthy, "10.0 GB free"),
            (HealthCheckNames.LogSink, HealthStatus.Healthy, "ok"));
        var health = new SystemHealth(ServiceReturning(report), new FakeTimeProvider(T0));

        var result = await health.GetAsync(fresh: true, TestContext.Current.CancellationToken);

        result.Overall.Should().Be(HealthLevel.Warning);
    }

    [Fact]
    public async Task A_check_missing_from_registration_counts_toward_Overall_exactly_as_displayed()
    {
        var report = ReportOf((HealthCheckNames.Database, HealthStatus.Healthy, "ready"));
        var health = new SystemHealth(ServiceReturning(report), new FakeTimeProvider(T0));

        var result = await health.GetAsync(fresh: true, TestContext.Current.CancellationToken);

        result.Items.Should().Contain(item => item.Name == HealthCheckNames.Backup && item.Level == HealthLevel.Failing);
        result.Overall.Should().Be(HealthLevel.Failing);
    }

    [Fact]
    public async Task Fresh_false_reuses_a_result_less_than_30_seconds_old()
    {
        var service = ServiceReturning(ReportOf((HealthCheckNames.Database, HealthStatus.Healthy, "ready")));
        var time = new FakeTimeProvider(T0);
        var health = new SystemHealth(service, time);
        await health.GetAsync(fresh: false, TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(29));
        await health.GetAsync(fresh: false, TestContext.Current.CancellationToken);

        await service.Received(1).CheckHealthAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fresh_false_re_runs_once_the_cache_turns_30_seconds_old()
    {
        var service = ServiceReturning(ReportOf((HealthCheckNames.Database, HealthStatus.Healthy, "ready")));
        var time = new FakeTimeProvider(T0);
        var health = new SystemHealth(service, time);
        await health.GetAsync(fresh: false, TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(30));
        await health.GetAsync(fresh: false, TestContext.Current.CancellationToken);

        await service.Received(2).CheckHealthAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_item_carries_its_checks_log_category()
    {
        var report = ReportOf((HealthCheckNames.Database, HealthStatus.Healthy, "ready"));
        var health = new SystemHealth(ServiceReturning(report), new FakeTimeProvider(T0));

        var result = await health.GetAsync(fresh: true, TestContext.Current.CancellationToken);

        result.Items.Single(i => i.Name == HealthCheckNames.Database).LogCategory
            .Should().Be("Noof.Ledger.Host.Startup.DatabaseStartupService");
    }

    [Fact]
    public async Task Fresh_true_always_runs_the_checks_again()
    {
        var service = ServiceReturning(ReportOf((HealthCheckNames.Database, HealthStatus.Healthy, "ready")));
        var health = new SystemHealth(service, new FakeTimeProvider(T0));

        await health.GetAsync(fresh: true, TestContext.Current.CancellationToken);
        await health.GetAsync(fresh: true, TestContext.Current.CancellationToken);

        await service.Received(2).CheckHealthAsync(Arg.Any<CancellationToken>());
    }
}
