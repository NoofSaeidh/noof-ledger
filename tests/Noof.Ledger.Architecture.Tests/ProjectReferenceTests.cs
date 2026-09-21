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
    public void Application_has_no_package_references()
    {
        Packages("Noof.Ledger.Application").Should().BeEmpty();
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

    [Fact]
    public void Web_package_references_are_exactly_its_allowed_set()
    {
        Packages("Noof.Ledger.Web").Should().BeEquivalentTo(
            "Microsoft.AspNetCore.Components.Web",
            "Microsoft.AspNetCore.Components.Authorization");
    }

    [Fact]
    public void Telegram_package_references_are_exactly_its_allowed_set()
    {
        Packages("Noof.Ledger.Telegram").Should().BeEquivalentTo(
            "Telegram.Bot",
            "Microsoft.Extensions.Http",
            "Microsoft.Extensions.Hosting.Abstractions");
    }

    [Fact]
    public void Web_has_no_program_cs()
    {
        var web = Path.Combine(RepoRoot.Find().FullName, "src", "Noof.Ledger.Web");

        Directory.EnumerateFiles(web, "Program.cs", SearchOption.AllDirectories)
            .Should().BeEmpty("Noof.Ledger.Web is a UI-only class library; the host owns startup");
    }

    [Fact]
    public void Web_is_a_razor_class_library_not_a_web_app()
    {
        Load("Noof.Ledger.Web").Root!.Attribute("Sdk")!.Value.Should().Be("Microsoft.NET.Sdk.Razor");
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
