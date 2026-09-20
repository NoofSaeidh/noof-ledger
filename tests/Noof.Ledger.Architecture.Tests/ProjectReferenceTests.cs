using System.Xml.Linq;
using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

public class ProjectReferenceTests
{
    [Theory]
    [InlineData("Noof.Ledger.Domain")]
    [InlineData("Noof.Ledger.Application", "Noof.Ledger.Domain")]
    [InlineData("Noof.Ledger.Persistence", "Noof.Ledger.Application", "Noof.Ledger.Domain")]
    [InlineData("Noof.Ledger.Web", "Noof.Ledger.Application", "Noof.Ledger.Domain")]
    [InlineData("Noof.Ledger.Telegram", "Noof.Ledger.Application", "Noof.Ledger.Domain")]
    [InlineData("Noof.Ledger.Ai", "Noof.Ledger.Application", "Noof.Ledger.Domain")]
    [InlineData("Noof.Ledger.Fx", "Noof.Ledger.Application", "Noof.Ledger.Domain")]
    [InlineData("Noof.Ledger.Receipts", "Noof.Ledger.Application", "Noof.Ledger.Domain")]
    [InlineData("Noof.Ledger.Host", "Noof.Ledger.Domain", "Noof.Ledger.Application", "Noof.Ledger.Persistence", "Noof.Ledger.Ai", "Noof.Ledger.Fx", "Noof.Ledger.Receipts", "Noof.Ledger.Telegram", "Noof.Ledger.Web")]
    public void Project_references_exactly_its_allowed_set(string project, params string[] allowed)
    {
        References(project).Should().BeEquivalentTo(allowed);
    }

    [Fact]
    public void Domain_has_no_package_references()
    {
        Packages("Noof.Ledger.Domain").Should().BeEmpty();
    }

    [Fact]
    public void Web_has_no_entity_framework_package()
    {
        Packages("Noof.Ledger.Web").Should().NotContain(p => p.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Web_has_no_http_client_package()
    {
        Packages("Noof.Ledger.Web").Should().NotContain(p =>
            p.Contains("Microsoft.Extensions.Http", StringComparison.OrdinalIgnoreCase) ||
            p.Contains("HttpClient", StringComparison.OrdinalIgnoreCase));
    }

    static XDocument Load(string project)
    {
        var path = Path.Combine(RepoRoot.Find().FullName, "src", project, $"{project}.csproj");
        return XDocument.Load(path);
    }

    static string[] References(string project) =>
        [.. Load(project).Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value.Replace('\\', '/')))];

    static string[] Packages(string project) =>
        [.. Load(project).Descendants("PackageReference")
            .Select(e => e.Attribute("Include")!.Value)];
}
