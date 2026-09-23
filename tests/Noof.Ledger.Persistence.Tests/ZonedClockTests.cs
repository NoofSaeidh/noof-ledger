using AwesomeAssertions;

namespace Noof.Ledger.Persistence.Tests;

public class ZonedClockTests
{
    [Theory]
    [InlineData("2026-09-21T21:50:00Z", "2026-09-21")]
    [InlineData("2026-09-21T22:30:00Z", "2026-09-22")]
    public void The_local_day_in_Belgrade_is_not_the_UTC_day(string instant, string expected)
    {
        ZonedClock.LocalDate(DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture), "Europe/Belgrade")
            .Should().Be(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }
}
