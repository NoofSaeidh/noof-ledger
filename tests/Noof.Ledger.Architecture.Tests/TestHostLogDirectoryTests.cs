using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

// I-3 (Phase 5 final review): a WebApplicationFactory<Program> that never sets
// Logging:File:Directory falls back to LoggingSetup's real default -
// %LOCALAPPDATA%\NoofLedger\logs, the operator's own log directory - and pollutes it with test
// noise. Every file that builds one must also route it through
// TestHostLogging.UseTempLogDirectory, not set the key by hand or leave it unset.
public class TestHostLogDirectoryTests
{
    [Fact]
    public void Every_test_host_in_Host_Tests_uses_the_shared_temp_log_directory_helper()
    {
        var hostTestsDirectory = Path.Combine(RepoRoot.Find().FullName, "tests", "Noof.Ledger.Host.Tests");

        var offenders = Directory.EnumerateFiles(hostTestsDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != "TestHostLogging.cs")
            .Select(path => (Path: path, Text: File.ReadAllText(path)))
            .Where(file => (file.Text.Contains("new WebApplicationFactory<Program>", StringComparison.Ordinal)
                    || file.Text.Contains("Noof.Ledger.Host.dll", StringComparison.Ordinal))
                && !file.Text.Contains("UseTempLogDirectory", StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file.Path))
            .ToArray();

        offenders.Should().BeEmpty(
            "every WebApplicationFactory<Program> build, and every directly-spawned "
            + "Noof.Ledger.Host.dll process, must call TestHostLogging.UseTempLogDirectory() "
            + "so the test host never writes into the operator's real log directory");
    }
}
