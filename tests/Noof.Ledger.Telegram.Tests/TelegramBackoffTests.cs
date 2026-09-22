using AwesomeAssertions;

namespace Noof.Ledger.Telegram.Tests;

public class TelegramBackoffTests
{
    [Fact]
    public void No_failures_means_no_delay()
    {
        TelegramBackoff.Compute(0).Should().Be(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    public void Backs_off_exponentially(int failures, int expectedSeconds)
    {
        TelegramBackoff.Compute(failures).Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void Never_waits_more_than_a_minute()
    {
        TelegramBackoff.Compute(10).Should().Be(TimeSpan.FromMinutes(1));
    }
}
