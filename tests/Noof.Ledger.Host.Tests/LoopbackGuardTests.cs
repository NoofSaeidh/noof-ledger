using AwesomeAssertions;
using Noof.Ledger.Host.Startup;

namespace Noof.Ledger.Host.Tests;

public class LoopbackGuardTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("http://localhost:5000")]
    [InlineData("http://[::1]:5000")]
    [InlineData("https://127.0.0.1:5001")]
    public void Loopback_is_allowed_while_auth_is_off(string address)
    {
        var act = () => LoopbackGuard.AssertSafe([address], "Off");

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("http://0.0.0.0:5000")]
    [InlineData("http://+:5000")]
    [InlineData("http://*:5000")]
    [InlineData("http://192.168.1.10:5000")]
    [InlineData("http://noof-desktop:5000")]
    public void A_non_loopback_binding_refuses_to_run_while_auth_is_off(string address)
    {
        var act = () => LoopbackGuard.AssertSafe([address], "Off");

        act.Should().Throw<InvalidOperationException>().WithMessage("*Auth:Mode*");
    }

    [Theory]
    [InlineData("http://0.0.0.0:5000")]
    [InlineData("http://192.168.1.10:5000")]
    [InlineData("http://+:5000")]
    public void The_same_bindings_are_fine_once_auth_is_on(string address)
    {
        var act = () => LoopbackGuard.AssertSafe([address], "Cookie");

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("")]
    [InlineData("None")]
    [InlineData("off ")]
    [InlineData("Disabled")]
    [InlineData("cookies")]
    public void An_unrecognised_mode_enforces_rather_than_skipping(string mode)
    {
        var act = () => LoopbackGuard.AssertSafe(["http://0.0.0.0:5000"], mode);

        act.Should().Throw<InvalidOperationException>(
            "an unrecognised mode registers the no-auth handler, so the interlock must not be skipped");
    }

    [Theory]
    [InlineData("http://[::]:5000")]
    [InlineData("http://not a url at all")]
    [InlineData("garbage")]
    public void Anything_not_demonstrably_loopback_fails_closed(string address)
    {
        var act = () => LoopbackGuard.AssertSafe([address], "Off");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void One_non_loopback_address_among_several_still_refuses()
    {
        var act = () => LoopbackGuard.AssertSafe(["http://127.0.0.1:5000", "http://192.168.1.10:5000"], "Off");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void No_bound_addresses_is_not_treated_as_safe_by_accident()
    {
        var act = () => LoopbackGuard.AssertSafe([], "Off");

        act.Should().NotThrow();
    }
}
