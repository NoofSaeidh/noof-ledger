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

    [Fact]
    public void A_Windows_time_zone_id_is_rejected_even_though_FindSystemTimeZoneById_accepts_it()
    {
        // TimeZoneInfo.FindSystemTimeZoneById also accepts a Windows id on Windows, but
        // time_zone_id is documented (Transaction.cs) and consumed downstream as IANA. Verified on
        // this machine: TryConvertWindowsIdToIanaId("Central Europe Standard Time", ...) succeeds
        // (-> "Europe/Budapest"), while TryConvertWindowsIdToIanaId("Europe/Belgrade", ...) fails --
        // that asymmetry is the cheapest available discriminator between the two id families.
        var act = () => CaptureTimeZoneGuard.Resolve("Central Europe Standard Time");

        act.Should().Throw<InvalidOperationException>().WithMessage("*Capture:TimeZone*");
    }
}
