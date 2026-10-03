using AwesomeAssertions;
using Noof.Ledger.Host.Cli;

namespace Noof.Ledger.Host.Tests;

public class BugsCommandParsingTests
{
    [Theory]
    [InlineData(new[] { "bugs", "export" }, false, null)]
    [InlineData(new[] { "bugs", "export", "--all" }, true, null)]
    [InlineData(new[] { "bugs", "export", "--output", "out/reports" }, false, "out/reports")]
    [InlineData(new[] { "bugs", "export", "--output", "out/reports", "--all" }, true, "out/reports")]
    [InlineData(new[] { "bugs", "export", "--all", "--output", "out/reports" }, true, "out/reports")]
    public void Recognises_export_and_its_options(string[] args, bool all, string? output)
    {
        BugsCommand.TryParse(args, out var arguments).Should().BeTrue();

        arguments.Should().Be(new BugsExportArguments(all, Path.GetFullPath(output ?? BugsCommand.DefaultOutputDirectory)));
    }

    // Every argument list that starts with "bugs" belongs to the verb, so a typo prints the usage line instead of
    // starting the web host with the typo as its arguments.
    [Theory]
    [InlineData(new object[] { new[] { "bugs" } })]
    [InlineData(new object[] { new[] { "bugs", "list" } })]
    [InlineData(new object[] { new[] { "bugs", "EXPORT" } })]
    [InlineData(new object[] { new[] { "bugs", "export", "--ALL" } })]
    [InlineData(new object[] { new[] { "bugs", "export", "--all", "--all" } })]
    [InlineData(new object[] { new[] { "bugs", "export", "--output" } })]
    [InlineData(new object[] { new[] { "bugs", "export", "--output", "" } })]
    [InlineData(new object[] { new[] { "bugs", "export", "--output", "--all" } })]
    [InlineData(new object[] { new[] { "bugs", "export", "--output", "a", "--output", "b" } })]
    [InlineData(new object[] { new[] { "bugs", "export", "extra" } })]
    public void Any_other_argument_list_starting_with_bugs_is_a_usage_error(string[] args)
    {
        BugsCommand.TryParse(args, out var arguments).Should().BeTrue();

        arguments.Should().BeNull();
    }

    [Theory]
    [InlineData(new object[] { new string[] { } })]
    [InlineData(new object[] { new[] { "bug", "export" } })]
    [InlineData(new object[] { new[] { "Bugs", "export" } })]
    [InlineData(new object[] { new[] { "--bugs" } })]
    [InlineData(new object[] { new[] { "user", "set-password", "noof" } })]
    [InlineData(new object[] { new[] { "--urls", "http://127.0.0.1:5000" } })]
    public void Leaves_every_other_argument_list_to_the_web_host(string[] args)
    {
        BugsCommand.TryParse(args, out var arguments).Should().BeFalse();

        arguments.Should().BeNull();
    }
}
