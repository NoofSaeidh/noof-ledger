using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

// Global constraint (Phase 5 SDD): "Noof.Ledger.Web has no EF, no HttpClient, no System.IO file
// access - it reads everything through Application interfaces." Nothing in Web may read a file
// itself.
public class WebFileSystemBoundaryTests
{
    static IEnumerable<FileInfo> WebSourceFiles() =>
        new DirectoryInfo(Path.Combine(RepoRoot.Find().FullName, "src", "Noof.Ledger.Web"))
            .EnumerateFiles("*.razor*", SearchOption.AllDirectories)
            .Concat(new DirectoryInfo(Path.Combine(RepoRoot.Find().FullName, "src", "Noof.Ledger.Web"))
                .EnumerateFiles("*.cs", SearchOption.AllDirectories));

    [Fact]
    public void No_source_file_uses_System_IO_directly()
    {
        var offenders = WebSourceFiles()
            .Where(file => File.ReadAllText(file.FullName).Contains("System.IO", StringComparison.Ordinal))
            .Select(file => file.Name)
            .ToList();

        offenders.Should().BeEmpty(
            "Web reads through Application interfaces, never a file directly - a File.* or " +
            "Directory.* call here would bypass that boundary");
    }

    [Fact]
    public void There_is_at_least_one_source_file_to_check()
    {
        WebSourceFiles().Should().NotBeEmpty("a rule whose subject set is empty passes forever and enforces nothing");
    }
}
