using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

// M-4 (Phase 5 final review): the log directory default was spelled three times
// (LoggingSetup.cs, LogFileTail.cs, DiskHealthCheck.cs - the last with a different Path.Combine
// spelling), so a future rename of the key or default could drift silently. Only LoggingSetup may
// spell the default; everything else resolves through LoggingSetup.ResolveLogDirectory.
public class LogDirectoryDefaultTests
{
    [Fact]
    public void Only_LoggingSetup_spells_the_default_log_directory()
    {
        var hostDirectory = Path.Combine(RepoRoot.Find().FullName, "src", "Noof.Ledger.Host");

        var offenders = Directory.EnumerateFiles(hostDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != "LoggingSetup.cs")
            .Select(path => (Path: path, Text: File.ReadAllText(path)))
            .Where(file => file.Text.Contains(@"NoofLedger\logs", StringComparison.Ordinal)
                || file.Text.Contains("\"NoofLedger\", \"logs\"", StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file.Path))
            .ToArray();

        offenders.Should().BeEmpty(
            "the log directory default must be spelled once, in LoggingSetup, and consumed through "
            + "LoggingSetup.ResolveLogDirectory everywhere else");
    }
}
