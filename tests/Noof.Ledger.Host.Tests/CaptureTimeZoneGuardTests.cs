using AwesomeAssertions;
using Noof.Ledger.Host.Startup;

namespace Noof.Ledger.Host.Tests;

public class CaptureTimeZoneGuardTests
{
    [Fact]
    public void The_configured_default_resolves_on_this_machine()
    {
        var act = () => CaptureTimeZoneGuard.Resolve("Europe/Belgrade");

        act.Should().NotThrow(".NET resolves IANA ids through ICU; this is the test that proves it works here, on this OS, rather than assuming it");
    }

    [Fact]
    public void An_unknown_zone_id_fails_loudly_with_a_clear_message()
    {
        var act = () => CaptureTimeZoneGuard.Resolve("Not/A/Real/Zone");

        act.Should().Throw<InvalidOperationException>().WithMessage("*Capture:TimeZone*");
    }
}
