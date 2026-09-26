using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

// Private fields in a .razor @code block are fields too. With the naming rules under [*.cs] only,
// Rider fell back to its own _underscore default for every component and flagged them there.
public class EditorConfigNamingTests
{
    [Theory]
    [InlineData("dotnet_naming_rule.private_fields_are_camel_case.style")]
    [InlineData("dotnet_naming_rule.private_constants_are_pascal_case.style")]
    [InlineData("dotnet_naming_rule.private_static_readonly_are_pascal_case.style")]
    public void Naming_rules_cover_razor_components_as_well_as_cs_files(string key)
    {
        var lines = File.ReadAllLines(Path.Combine(RepoRoot.Find().FullName, ".editorconfig"));

        var index = Array.FindIndex(lines, line => line.TrimStart().StartsWith(key, StringComparison.Ordinal));
        index.Should().BeGreaterThanOrEqualTo(0, $"{key} must be set in .editorconfig");

        var section = lines.Take(index).Last(line => line.TrimStart().StartsWith('['));
        section.Trim().Should().Be("[*.{cs,razor}]");
    }
}
