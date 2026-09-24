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

    [Theory]
    [InlineData("2026-09-01", "2026-08-31T22:00:00Z")]
    [InlineData("2026-01-15", "2026-01-14T23:00:00Z")]
    public void The_start_of_a_Belgrade_day_is_its_local_midnight_as_a_UTC_instant(string day, string expected)
    {
        var start = ZonedClock.StartOfDay(
            DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture), "Europe/Belgrade");

        start.Should().Be(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
        start.Offset.Should().Be(TimeSpan.Zero, "Npgsql writes only offset-zero values to timestamptz");
    }
}
