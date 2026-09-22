using AwesomeAssertions;
using Noof.Ledger.Host.Workers;

namespace Noof.Ledger.Host.Tests;

public class CategorizationWorkerOptionsTests
{
    [Fact]
    public void Defaults_match_the_documented_values()
    {
        var options = new CategorizationWorkerOptions();

        options.Lease.Should().Be(TimeSpan.FromMinutes(5));
        options.MaxAttempts.Should().Be(8);
        options.PollInterval.Should().Be(TimeSpan.FromSeconds(5));
        options.BackoffBase.Should().Be(TimeSpan.FromSeconds(30));
        options.BackoffCap.Should().Be(TimeSpan.FromMinutes(64));
        options.MerchantHintLimit.Should().Be(10);
        options.MaxCanonicalizationsPerJob.Should().Be(3);
        options.AccountCooldown.Should().Be(TimeSpan.FromMinutes(5));
        options.DefaultCurrency.Should().Be("RSD");
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    [InlineData(5, 480)]
    [InlineData(6, 960)]
    [InlineData(7, 1920)]
    [InlineData(8, 3840)]
    public void ComputeBackoff_follows_the_documented_30_second_doubling_schedule(int attemptCount, int expectedSeconds)
    {
        var options = new CategorizationWorkerOptions();

        var backoff = options.ComputeBackoff(attemptCount);

        backoff.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void ComputeBackoff_is_capped_even_when_MaxAttempts_is_raised_past_the_documented_schedule()
    {
        var options = new CategorizationWorkerOptions { BackoffCap = TimeSpan.FromMinutes(1) };

        var backoff = options.ComputeBackoff(attemptCount: 8);

        backoff.Should().Be(TimeSpan.FromMinutes(1), "the cap must actually truncate, not just happen to match the schedule at defaults");
    }
}
