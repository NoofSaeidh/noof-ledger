using System.Xml.Linq;
using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

public class ToolsBoundaryTests
{
    static XDocument[] SourceProjects() =>
        [.. Directory.EnumerateFiles(Path.Combine(RepoRoot.Find().FullName, "src"), "*.csproj", SearchOption.AllDirectories)
            .Select(XDocument.Load)];

    [Fact]
    public void No_src_project_references_a_tool()
    {
        var references = SourceProjects()
            .SelectMany(project => project.Descendants("ProjectReference"))
            .Select(reference => reference.Attribute("Include")!.Value.Replace('\\', '/'))
            .ToList();

        references.Should().NotBeEmpty();
        references.Should().NotContain(include => include.Contains("/tools/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Only_tests_and_the_demo_tool_see_src_internals()
    {
        var targets = SourceProjects()
            .SelectMany(project => project.Descendants("InternalsVisibleTo"))
            .Select(entry => entry.Attribute("Include")!.Value)
            .ToList();

        targets.Should().Contain("Noof.Ledger.Demo");
        targets.Should().OnlyContain(target =>
            target.EndsWith(".Tests", StringComparison.Ordinal)
            || target == "DynamicProxyGenAssembly2"
            || target == "Noof.Ledger.Demo");
    }
}
