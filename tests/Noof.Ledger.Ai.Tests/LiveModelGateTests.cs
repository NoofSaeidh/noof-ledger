using AwesomeAssertions;

namespace Noof.Ledger.Ai.Tests;

// Not part of the live suite - this class runs on every machine, every time, with no key and no
// network. It is the proof that the opt-in mechanism itself cannot silently start spending money:
// if TryParse ever started treating an absent or blank variable as "found", this is what would
// turn red and stop it before it reached LiveModelTests.
public sealed class LiveModelGateTests
{
    [Fact]
    public void An_absent_variable_is_reported_as_not_found()
    {
        LiveModelGate.TryParse(null, out _).Should().BeFalse();
    }

    [Fact]
    public void A_blank_or_whitespace_variable_is_reported_as_not_found()
    {
        LiveModelGate.TryParse("   ", out _).Should().BeFalse();
    }

    [Fact]
    public void A_real_looking_value_is_reported_as_found_and_returned_unchanged()
    {
        LiveModelGate.TryParse("sk-ant-test-value", out var apiKey).Should().BeTrue();
        apiKey.Should().Be("sk-ant-test-value");
    }
}
