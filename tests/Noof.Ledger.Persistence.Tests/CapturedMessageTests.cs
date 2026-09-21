using AwesomeAssertions;
using Noof.Ledger.Application.Capture;

namespace Noof.Ledger.Persistence.Tests;

public class CapturedMessageTests
{
    [Fact]
    public void Constructing_with_a_non_UTC_SentAt_throws_immediately_instead_of_failing_later_at_the_database()
    {
        // Before this guard, a non-UTC SentAt was accepted silently here and only failed much
        // later, inside EfCaptureStore.CaptureAsync's SaveChangesAsync, with a confusing Npgsql
        // message that names neither CapturedMessage nor SentAt. Fail at construction instead.
        var nonUtc = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(2));

        var act = () => new CapturedMessage(1, 100, "coffee 3.50", nonUtc);

        act.Should().Throw<ArgumentException>().WithMessage("*UTC*").And.ParamName.Should().Be("SentAt");
    }

    [Fact]
    public void Constructing_with_a_UTC_SentAt_succeeds()
    {
        var utc = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        var message = new CapturedMessage(1, 100, "coffee 3.50", utc);

        message.SentAt.Should().Be(utc);
    }
}
