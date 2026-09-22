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
    public void Loopback_is_allowed(string address)
    {
        var act = () => LoopbackGuard.AssertSafe([address]);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("http://0.0.0.0:5000")]
    [InlineData("http://+:5000")]
    [InlineData("http://*:5000")]
    [InlineData("http://192.168.1.10:5000")]
    [InlineData("http://noof-desktop:5000")]
    public void A_non_loopback_binding_always_refuses_to_run(string address)
    {
        var act = () => LoopbackGuard.AssertSafe([address]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*loopback*");
    }

    [Theory]
    [InlineData("http://[::]:5000")]
    [InlineData("http://not a url at all")]
    [InlineData("garbage")]
    public void Anything_not_demonstrably_loopback_fails_closed(string address)
    {
        var act = () => LoopbackGuard.AssertSafe([address]);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void One_non_loopback_address_among_several_still_refuses()
    {
        var act = () => LoopbackGuard.AssertSafe(["http://127.0.0.1:5000", "http://192.168.1.10:5000"]);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void No_bound_addresses_is_not_treated_as_safe_by_accident()
    {
        var act = () => LoopbackGuard.AssertSafe([]);

        act.Should().NotThrow();
    }
}
