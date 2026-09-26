using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

// Scope item for Phase 5 Task 6: the LLM plays no part in health. AiKeysHealthCheck reads
// IModelProvider/ISpeechProvider - both Application interfaces - never Noof.Ledger.Ai's own model
// or speech types directly.
public class HealthCheckBoundaryTests
{
    static readonly string SrcRoot = Path.Combine(RepoRoot.Find().FullName, "src");
    static readonly Regex HealthCheckDeclaration = new(@"[:,]\s*ISystemHealthCheck\b", RegexOptions.Compiled);

    [Fact]
    public void No_health_check_reaches_a_model()
    {
        var healthCheckFiles = Directory.EnumerateFiles(SrcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => HealthCheckDeclaration.IsMatch(File.ReadAllText(file)))
            .ToArray();

        var offenders = healthCheckFiles
            .Where(file =>
            {
                var text = File.ReadAllText(file);
                return text.Contains("Microsoft.Extensions.AI", StringComparison.Ordinal)
                    || text.Contains("IChatClient", StringComparison.Ordinal)
                    || text.Contains("ISpeechToTextClient", StringComparison.Ordinal)
                    || text.Contains("ICategorizer", StringComparison.Ordinal)
                    || text.Contains("ITranscriber", StringComparison.Ordinal);
            })
            .Select(file => Path.GetRelativePath(SrcRoot, file))
            .ToArray();

        offenders.Should().BeEmpty(
            "health checks read IModelProvider/ISpeechProvider, never the model or speech client stack directly");
        healthCheckFiles.Should().NotBeEmpty(
            "the ISystemHealthCheck pattern must find the real health checks, or an empty offender list proves nothing");
        healthCheckFiles.Select(file => Path.GetRelativePath(SrcRoot, file))
            .Should().Contain(file => file.StartsWith("Noof.Ledger.Ai" + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                "AiKeysHealthCheck must still be found under src/Noof.Ledger.Ai/");
    }

    // Host is Sdk.Web and still gets Microsoft.Extensions.Diagnostics.HealthChecks's types through
    // the ASP.NET shared framework, so a compile-time reference is not what proves this - a text
    // scan of what src actually names and depends on is.
    [Fact]
    public void Nothing_in_src_uses_Microsoft_health_checks()
    {
        var repoRoot = RepoRoot.Find().FullName;
        var candidates = Directory.EnumerateFiles(SrcRoot, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(SrcRoot, "*.csproj", SearchOption.AllDirectories))
            .Append(Path.Combine(repoRoot, "Directory.Packages.props"))
            .ToArray();

        var ihealthCheck = new Regex(@"\bIHealthCheck\b", RegexOptions.Compiled);

        var offenders = candidates
            .Where(file =>
            {
                var text = File.ReadAllText(file);
                return text.Contains("Microsoft.Extensions.Diagnostics.HealthChecks", StringComparison.Ordinal)
                    || text.Contains("AddHealthChecks(", StringComparison.Ordinal)
                    || ihealthCheck.IsMatch(text);
            })
            .Select(file => Path.GetRelativePath(repoRoot, file))
            .ToArray();

        offenders.Should().BeEmpty();
    }
}
