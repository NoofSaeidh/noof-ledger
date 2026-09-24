using AwesomeAssertions;
using Noof.Ledger.Application.Capture;

namespace Noof.Ledger.Persistence.Tests;

public class CapturedVoiceTests
{
    [Fact]
    public void Refuses_a_send_time_that_is_not_utc()
    {
        var act = () => new CapturedVoice(111, 5, "voice-file-1", 4, new DateTimeOffset(2026, 9, 24, 11, 0, 0, TimeSpan.FromHours(2)));

        act.Should().Throw<ArgumentException>().WithParameterName("SentAt");
    }
}
