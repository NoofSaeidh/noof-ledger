using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.TestKit;

namespace Noof.Ledger.Host.Tests.Diagnostics;

// Task V2 (verbose design B1): the mechanism every timed operation site goes through - GetTimestamp
// / GetElapsedTime off a TimeProvider, never Stopwatch, never a GetUtcNow subtraction.
public class OperationTimerTests
{
    static SlowOperationOptions Options()
    {
        var options = new SlowOperationOptions();
        options.ThresholdMs.Clear();
        options.ThresholdMs["default"] = 1000;
        options.ThresholdMs["telegram"] = 3000;
        return options;
    }

    static (OperationTimer Timer, FakeTimeProvider Clock, CapturingLogger<OperationTimerTests> Logger) Create(SlowOperationOptions? options = null)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var logger = new CapturingLogger<OperationTimerTests>();
        var timer = new OperationTimer(clock, options ?? Options());
        return (timer, clock, logger);
    }

    [Fact]
    public void Below_threshold_logs_5301_debug_with_operation_and_elapsed()
    {
        var (timer, clock, logger) = Create();

        using var timing = timer.Start(logger, "widget.sync");
        clock.Advance(TimeSpan.FromMilliseconds(400));
        timing.Stop();

        logger.Entries.Should().ContainSingle();
        var entry = logger.Entries[0];
        entry.EventId.Id.Should().Be(5301);
        entry.Level.Should().Be(LogLevel.Debug);
        entry.Properties["Operation"].Should().Be("widget.sync");
        entry.Properties["ElapsedMs"].Should().Be(400L);
    }

    [Fact]
    public void At_or_over_threshold_logs_5302_warning_with_threshold_and_no_5301()
    {
        var (timer, clock, logger) = Create();

        using var timing = timer.Start(logger, "widget.sync");
        clock.Advance(TimeSpan.FromMilliseconds(1000));
        timing.Stop();

        logger.Entries.Should().ContainSingle();
        var entry = logger.Entries[0];
        entry.EventId.Id.Should().Be(5302);
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Properties["ThresholdMs"].Should().Be(1000L);
    }

    [Fact]
    public void OnlyIfSlow_and_fast_logs_nothing()
    {
        var (timer, clock, logger) = Create();

        using var timing = timer.Start(logger, "widget.sync");
        clock.Advance(TimeSpan.FromMilliseconds(10));
        timing.Stop(onlyIfSlow: true);

        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public void OnlyIfSlow_and_slow_logs_5302()
    {
        var (timer, clock, logger) = Create();

        using var timing = timer.Start(logger, "widget.sync");
        clock.Advance(TimeSpan.FromMilliseconds(2000));
        timing.Stop(onlyIfSlow: true);

        logger.Entries.Should().ContainSingle();
        logger.Entries[0].EventId.Id.Should().Be(5302);
    }

    [Fact]
    public void Dispose_after_stop_logs_once()
    {
        var (timer, clock, logger) = Create();

        var timing = timer.Start(logger, "widget.sync");
        clock.Advance(TimeSpan.FromMilliseconds(10));
        timing.Stop();
        timing.Dispose();

        logger.Entries.Should().ContainSingle();
    }

    [Fact]
    public void Stop_called_twice_returns_first_elapsed_value_and_logs_once()
    {
        var (timer, clock, logger) = Create();

        using var timing = timer.Start(logger, "widget.sync");
        clock.Advance(TimeSpan.FromMilliseconds(10));
        var first = timing.Stop();
        clock.Advance(TimeSpan.FromMilliseconds(500));
        var second = timing.Stop();

        second.Should().Be(first);
        logger.Entries.Should().ContainSingle();
    }

    [Fact]
    public void Dispose_without_stop_still_logs()
    {
        var (timer, clock, logger) = Create();

        try
        {
            using var timing = timer.Start(logger, "widget.sync");
            clock.Advance(TimeSpan.FromMilliseconds(10));
            throw new InvalidOperationException("simulated failure");
        }
        catch (InvalidOperationException)
        {
            // expected: the using block's Dispose still records the duration.
        }

        logger.Entries.Should().ContainSingle();
    }

    [Fact]
    public void ExpectedWait_is_added_to_the_threshold()
    {
        var options = Options();
        options.ThresholdMs["telegram"] = 3000;
        var (timer, clock, logger) = Create(options);

        using var timing = timer.Start(logger, "telegram.getUpdates", TimeSpan.FromSeconds(90));
        clock.Advance(TimeSpan.FromSeconds(92));
        timing.Stop(onlyIfSlow: true);

        logger.Entries.Should().BeEmpty("92 s is below the 93 s threshold (3 s + 90 s expected wait)");
    }

    [Fact]
    public void ExpectedWait_over_threshold_logs_5302_with_combined_threshold()
    {
        var options = Options();
        options.ThresholdMs["telegram"] = 3000;
        var (timer, clock, logger) = Create(options);

        using var timing = timer.Start(logger, "telegram.getUpdates", TimeSpan.FromSeconds(90));
        clock.Advance(TimeSpan.FromSeconds(94));
        timing.Stop(onlyIfSlow: true);

        logger.Entries.Should().ContainSingle();
        var entry = logger.Entries[0];
        entry.EventId.Id.Should().Be(5302);
        entry.Properties["ThresholdMs"].Should().Be(93000L);
    }

    [Fact]
    public void Lookup_exact_key_beats_group()
    {
        var options = Options();
        options.ThresholdMs["job"] = 60000;
        options.ThresholdMs["job.queueWait"] = 30000;
        var (timer, clock, logger) = Create(options);

        using var timing = timer.Start(logger, "job.queueWait");
        clock.Advance(TimeSpan.FromMilliseconds(31000));
        timing.Stop();

        logger.Entries[0].EventId.Id.Should().Be(5302);
        logger.Entries[0].Properties["ThresholdMs"].Should().Be(30000L);
    }

    [Fact]
    public void Lookup_group_beats_default()
    {
        var options = Options();
        options.ThresholdMs["job"] = 60000;
        var (timer, clock, logger) = Create(options);

        using var timing = timer.Start(logger, "job.queueWait");
        clock.Advance(TimeSpan.FromMilliseconds(45000));
        timing.Stop(onlyIfSlow: true);

        logger.Entries.Should().BeEmpty("job's group threshold (60000) covers this, not the 1000 ms default");
    }

    [Fact]
    public void Lookup_keys_are_case_insensitive()
    {
        var options = Options();
        options.ThresholdMs["MODEL"] = 45000;
        var (timer, clock, logger) = Create(options);

        using var timing = timer.Start(logger, "model.categorize");
        clock.Advance(TimeSpan.FromMilliseconds(46000));
        timing.Stop();

        logger.Entries[0].Properties["ThresholdMs"].Should().Be(45000L);
    }

    [Fact]
    public void Lookup_empty_dictionary_falls_back_to_1000()
    {
        var options = new SlowOperationOptions();
        options.ThresholdMs.Clear();
        var (timer, clock, logger) = Create(options);

        using var timing = timer.Start(logger, "widget.sync");
        clock.Advance(TimeSpan.FromMilliseconds(1000));
        timing.Stop();

        logger.Entries[0].Properties["ThresholdMs"].Should().Be(1000L);
    }

    [Fact]
    public void Record_with_explicit_elapsed_behaves_the_same_way()
    {
        var (timer, _, logger) = Create();

        ((IOperationTimer)timer).Record(logger, "widget.sync", TimeSpan.FromMilliseconds(1500));

        logger.Entries.Should().ContainSingle();
        logger.Entries[0].EventId.Id.Should().Be(5302);
        logger.Entries[0].Properties["ElapsedMs"].Should().Be(1500L);
    }
}
