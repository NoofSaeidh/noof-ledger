using AwesomeAssertions;
using Noof.Ledger.Host.Cli;

namespace Noof.Ledger.Host.Tests;

public class CliVerbParsingTests
{
    [Theory]
    [InlineData(new[] { "user", "set-password", "noof" }, "noof")]
    [InlineData(new[] { "user", "set-password", "someone-else" }, "someone-else")]
    public void Recognises_the_verb_and_extracts_the_username(string[] args, string expected)
    {
        UserCommand.TryParse(args, out var username).Should().BeTrue();
        username.Should().Be(expected);
    }

    [Theory]
    [InlineData(new object[] { new string[] { } })]
    [InlineData(new object[] { new[] { "user" } })]
    [InlineData(new object[] { new[] { "user", "set-password" } })]
    [InlineData(new object[] { new[] { "users", "set-password", "noof" } })]
    [InlineData(new object[] { new[] { "user", "setpassword", "noof" } })]
    [InlineData(new object[] { new[] { "--urls", "http://127.0.0.1:5000" } })]
    public void Rejects_everything_else(string[] args)
    {
        UserCommand.TryParse(args, out _).Should().BeFalse();
    }
}
