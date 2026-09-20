using System.Xml.Linq;
using AwesomeAssertions;

namespace Noof.Architecture.Tests;

public class ProjectReferenceTests
{
    [Theory]
    [InlineData("Noof.Domain")]
    [InlineData("Noof.Application", "Noof.Domain")]
    [InlineData("Noof.Persistence", "Noof.Application", "Noof.Domain")]
    [InlineData("Noof.Web", "Noof.Application", "Noof.Domain")]
    [InlineData("Noof.Telegram", "Noof.Application", "Noof.Domain")]
    [InlineData("Noof.Ai", "Noof.Application", "Noof.Domain")]
    [InlineData("Noof.Fx", "Noof.Application", "Noof.Domain")]
    [InlineData("Noof.Receipts", "Noof.Application", "Noof.Domain")]
    [InlineData("Noof.Host", "Noof.Domain", "Noof.Application", "Noof.Persistence", "Noof.Ai", "Noof.Fx", "Noof.Receipts", "Noof.Telegram", "Noof.Web")]
    public void Project_references_exactly_its_allowed_set(string project, params string[] allowed)
    {
        References(project).Should().BeEquivalentTo(allowed);
    }

    [Fact]
    public void Domain_has_no_package_references()
    {
        Packages("Noof.Domain").Should().BeEmpty();
    }

    [Fact]
    public void Web_has_no_entity_framework_package()
    {
        Packages("Noof.Web").Should().NotContain(p => p.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Web_has_no_http_client_package()
    {
        Packages("Noof.Web").Should().NotContain(p =>
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
