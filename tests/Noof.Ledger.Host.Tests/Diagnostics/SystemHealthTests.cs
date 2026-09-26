using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Host.Diagnostics;

namespace Noof.Ledger.Host.Tests.Diagnostics;

public class SystemHealthTests
{
    static readonly DateTimeOffset T0 = new(2026, 9, 25, 3, 0, 0, TimeSpan.Zero);

    static SystemHealth HealthOver(FakeTimeProvider time, ILoggerFactory loggerFactory, params ISystemHealthCheck[] checks)
    {
        var services = new ServiceCollection();
        foreach (var check in checks)
            services.AddScoped<ISystemHealthCheck>(_ => check);
        var provider = services.BuildServiceProvider();
        return new SystemHealth(provider.GetRequiredService<IServiceScopeFactory>(), time, loggerFactory);
    }

    [Fact]
    public async Task Items_are_ordered_by_Order_not_registration_order()
    {
        var a = new FakeCheck("A", 30, _ => Task.FromResult(HealthOutcome.Ok("ok")));
        var b = new FakeCheck("B", 10, _ => Task.FromResult(HealthOutcome.Ok("ok")));
        var c = new FakeCheck("C", 20, _ => Task.FromResult(HealthOutcome.Ok("ok")));
        var health = HealthOver(new FakeTimeProvider(T0), NullLoggerFactory.Instance, a, b, c);

        var result = await health.GetAsync(fresh: true, TestContext.Current.CancellationToken);

        result.Items.Select(i => i.Name).Should().BeEquivalentTo(["B", "C", "A"], options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task Equal_Order_falls_back_to_Name()
    {
        var zeta = new FakeCheck("Zeta", 10, _ => Task.FromResult(HealthOutcome.Ok("ok")));
        var alpha = new FakeCheck("Alpha", 10, _ => Task.FromResult(HealthOutcome.Ok("ok")));
        var health = HealthOver(new FakeTimeProvider(T0), NullLoggerFactory.Instance, zeta, alpha);

        var result = await health.GetAsync(fresh: true, TestContext.Current.CancellationToken);

        result.Items.Select(i => i.Name).Should().BeEquivalentTo(["Alpha", "Zeta"], options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task An_item_carries_name_level_summary_and_log_category()
    {
        var check = new FakeCheck("Probe", 10, _ => Task.FromResult(HealthOutcome.Warning("stale")), logCategory: "Noof.Ledger.Tests.Probe");
        var health = HealthOver(new FakeTimeProvider(T0), NullLoggerFactory.Instance, check);

        var result = await health.GetAsync(fresh: true, TestContext.Current.CancellationToken);

        var item = result.Items.Single();
        item.Name.Should().Be("Probe");
        item.Level.Should().Be(HealthLevel.Warning);
        item.Summary.Should().Be("stale");
        item.LogCategory.Should().Be("Noof.Ledger.Tests.Probe");
    }

    [Fact]
    public async Task Overall_is_the_worst_item()
    {
        var ok = new FakeCheck("Ok", 10, _ => Task.FromResult(HealthOutcome.Ok("ok")));
        var warning = new FakeCheck("Warn", 20, _ => Task.FromResult(HealthOutcome.Warning("meh")));
        var health = HealthOver(new FakeTimeProvider(T0), NullLoggerFactory.Instance, ok, warning);

        var result = await health.GetAsync(fresh: true, TestContext.Current.CancellationToken);

        result.Overall.Should().Be(HealthLevel.Warning);
    }

    [Fact]
    public async Task Overall_is_Ok_when_nothing_is_registered()
    {
        var health = HealthOver(new FakeTimeProvider(T0), NullLoggerFactory.Instance);

        var result = await health.GetAsync(fresh: true, TestContext.Current.CancellationToken);

        result.Overall.Should().Be(HealthLevel.Ok);
        result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_check_that_throws_is_Failing_with_its_type_and_never_its_message()
    {
        var throwing = new FakeCheck("Throws", 10,
            _ => throw new InvalidOperationException("password=hunter2"));
        var later = new FakeCheck("Later", 20, _ => Task.FromResult(HealthOutcome.Ok("ok")));
        var health = HealthOver(new FakeTimeProvider(T0), NullLoggerFactory.Instance, throwing, later);

        var result = await health.GetAsync(fresh: true, TestContext.Current.CancellationToken);

        var item = result.Items.Single(i => i.Name == "Throws");
        item.Level.Should().Be(HealthLevel.Failing);
        item.Summary.Should().Be("Check failed (InvalidOperationException) — see logs");
        item.Summary.Should().NotContain("hunter2");
        later.Runs.Should().Be(1);
    }

    [Fact]
    public async Task A_check_that_throws_is_logged_under_its_own_LogCategory_as_5104()
    {
        var throwing = new FakeCheck("Throws", 10,
            _ => throw new InvalidOperationException("boom"), logCategory: "Noof.Ledger.Tests.Throws");
        var recorder = new RecordingLoggerFactory();
        var health = HealthOver(new FakeTimeProvider(T0), recorder, throwing);

        await health.GetAsync(fresh: true, TestContext.Current.CancellationToken);

        recorder.Entries.Should().ContainSingle(e =>
            e.Category == "Noof.Ledger.Tests.Throws" && e.EventId.Id == 5104 && e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task A_check_without_an_answer_in_5_seconds_is_Failing_and_logged_as_5105()
    {
        var time = new FakeTimeProvider(T0);
        var recorder = new RecordingLoggerFactory();
        var hung = new FakeCheck("Hung", 10, async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return HealthOutcome.Ok("unreachable");
        }, logCategory: "Noof.Ledger.Tests.Hung");
        var later = new FakeCheck("Later", 20, _ => Task.FromResult(HealthOutcome.Ok("ok")));
        var health = HealthOver(time, recorder, hung, later);

        var task = health.GetAsync(fresh: true, TestContext.Current.CancellationToken);
        time.Advance(SystemHealth.CheckTimeout);
        var result = await task;

        var item = result.Items.Single(i => i.Name == "Hung");
        item.Level.Should().Be(HealthLevel.Failing);
        item.Summary.Should().Be("No answer within 5 s");
        later.Runs.Should().Be(1);
        recorder.Entries.Should().ContainSingle(e =>
            e.Category == "Noof.Ledger.Tests.Hung" && e.EventId.Id == 5105 && e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_nothing_is_cached()
    {
        using var cts = new CancellationTokenSource();
        var callCount = 0;
        var slow = new FakeCheck("Slow", 10, async ct =>
        {
            callCount++;
            if (callCount == 1)
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return HealthOutcome.Ok("ok");
        });
        var health = HealthOver(new FakeTimeProvider(T0), NullLoggerFactory.Instance, slow);

        var task = health.GetAsync(fresh: true, cts.Token);
        await cts.CancelAsync();

        var act = async () => await task;
        await act.Should().ThrowAsync<OperationCanceledException>();

        var later = await health.GetAsync(fresh: false, TestContext.Current.CancellationToken);
        later.Items.Should().ContainSingle(i => i.Name == "Slow" && i.Level == HealthLevel.Ok);
        callCount.Should().Be(2, "the cancelled run must not be cached, so the next call re-runs the check");
    }

    [Fact]
    public async Task Checks_run_one_at_a_time()
    {
        var gateForA = new TaskCompletionSource();
        var b = new FakeCheck("B", 20, _ => Task.FromResult(HealthOutcome.Ok("ok")));
        var a = new FakeCheck("A", 10, async _ =>
        {
            b.Runs.Should().Be(0, "B must not start before A finishes");
            await gateForA.Task;
            return HealthOutcome.Ok("ok");
        });
        var health = HealthOver(new FakeTimeProvider(T0), NullLoggerFactory.Instance, a, b);

        var task = health.GetAsync(fresh: true, TestContext.Current.CancellationToken);
        b.Runs.Should().Be(0);
        gateForA.SetResult();
        await task;

        b.Runs.Should().Be(1);
    }

    [Fact]
    public async Task Each_run_resolves_checks_from_a_fresh_scope()
    {
        var services = new ServiceCollection();
        services.AddScoped<ScopeProbe>();
        services.AddScoped<ISystemHealthCheck>(sp => new ProbeCheck(sp.GetRequiredService<ScopeProbe>(), "First", 10));
        services.AddScoped<ISystemHealthCheck>(sp => new ProbeCheck(sp.GetRequiredService<ScopeProbe>(), "Second", 20));
        var provider = services.BuildServiceProvider();
        var health = new SystemHealth(provider.GetRequiredService<IServiceScopeFactory>(), new FakeTimeProvider(T0), NullLoggerFactory.Instance);

        var first = await health.GetAsync(fresh: true, TestContext.Current.CancellationToken);
        var second = await health.GetAsync(fresh: true, TestContext.Current.CancellationToken);

        first.Items[0].Summary.Should().Be(first.Items[1].Summary, "both checks in the same run share one scope");
        first.Items[0].Summary.Should().NotBe(second.Items[0].Summary, "each run gets a fresh scope");
    }

    [Fact]
    public async Task Fresh_false_reuses_a_result_less_than_30_seconds_old()
    {
        var check = new FakeCheck("Database", 10, _ => Task.FromResult(HealthOutcome.Ok("ready")));
        var time = new FakeTimeProvider(T0);
        var health = HealthOver(time, NullLoggerFactory.Instance, check);
        await health.GetAsync(fresh: false, TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(29));
        await health.GetAsync(fresh: false, TestContext.Current.CancellationToken);

        check.Runs.Should().Be(1);
    }

    [Fact]
    public async Task Fresh_false_re_runs_once_the_cache_turns_30_seconds_old()
    {
        var check = new FakeCheck("Database", 10, _ => Task.FromResult(HealthOutcome.Ok("ready")));
        var time = new FakeTimeProvider(T0);
        var health = HealthOver(time, NullLoggerFactory.Instance, check);
        await health.GetAsync(fresh: false, TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(30));
        await health.GetAsync(fresh: false, TestContext.Current.CancellationToken);

        check.Runs.Should().Be(2);
    }

    [Fact]
    public async Task Fresh_true_always_runs_the_checks_again()
    {
        var check = new FakeCheck("Database", 10, _ => Task.FromResult(HealthOutcome.Ok("ready")));
        var health = HealthOver(new FakeTimeProvider(T0), NullLoggerFactory.Instance, check);

        await health.GetAsync(fresh: true, TestContext.Current.CancellationToken);
        await health.GetAsync(fresh: true, TestContext.Current.CancellationToken);

        check.Runs.Should().Be(2);
    }
}
