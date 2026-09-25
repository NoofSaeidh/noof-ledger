using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

// I-3 (Phase 5 final review): run.ps1 test fast excludes Host.Tests classes that clone
// noof_ledger_test_template by filtering on [Trait("Category", "Database")], not by class name -
// a class name list drifts the moment someone adds or renames a database-backed test. This is that
// filter's own detector: any Host.Tests class that opens the shared PostgreSQL server the same way
// (CreateCloneAsync against DatabaseSettings.TemplateDatabase, guarded by DatabaseIsReachableAsync)
// must carry the trait, or `test fast` collides with it outside the suite lock.
public class HostDatabaseTestTraitTests
{
    [Fact]
    public void Every_Host_Tests_class_that_clones_the_test_template_carries_the_Database_trait()
    {
        var hostTestsDirectory = Path.Combine(RepoRoot.Find().FullName, "tests", "Noof.Ledger.Host.Tests");
        var files = Directory.EnumerateFiles(hostTestsDirectory, "*.cs", SearchOption.AllDirectories).ToList();

        var databaseBackedFiles = files
            .Select(path => (Path: path, Text: File.ReadAllText(path)))
            .Where(f => f.Text.Contains("DatabaseIsReachableAsync") && f.Text.Contains("DatabaseSettings.TemplateDatabase"))
            .ToList();

        databaseBackedFiles.Should().NotBeEmpty(
            "this is the detector's own subject set - if it is ever empty, the pattern below stopped matching anything");

        foreach (var (path, text) in databaseBackedFiles)
        {
            text.Should().Contain(
                """[Trait("Category", "Database")]""",
                $"{Path.GetFileName(path)} clones the test template and must carry the Database trait so run.ps1 test fast can exclude it");
        }
    }
}
