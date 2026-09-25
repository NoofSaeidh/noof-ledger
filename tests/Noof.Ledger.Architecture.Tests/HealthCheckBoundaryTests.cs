using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

// Scope item for Phase 5 Task 6: the LLM plays no part in health. AiKeysHealthCheck reads
// IModelProvider/ISpeechProvider - both Application interfaces - never Noof.Ledger.Ai directly.
public class HealthCheckBoundaryTests
{
    static readonly string SrcRoot = Path.Combine(RepoRoot.Find().FullName, "src");

    [Fact]
    public void No_health_check_references_the_AI_stack()
    {
        var healthCheckFiles = Directory.EnumerateFiles(SrcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file =>
            {
                var text = File.ReadAllText(file);
                return text.Contains(": IHealthCheck", StringComparison.Ordinal)
                    || text.Contains(", IHealthCheck", StringComparison.Ordinal);
            })
            .ToArray();

        var offenders = healthCheckFiles
            .Where(file =>
            {
                var text = File.ReadAllText(file);
                return text.Contains("Noof.Ledger.Ai", StringComparison.Ordinal)
                    || text.Contains("Microsoft.Extensions.AI", StringComparison.Ordinal);
            })
            .Select(file => Path.GetRelativePath(SrcRoot, file))
            .ToArray();

        offenders.Should().BeEmpty(
            "health checks read IModelProvider/ISpeechProvider, never the AI assembly or Microsoft.Extensions.AI directly");
        healthCheckFiles.Should().NotBeEmpty(
            "the IHealthCheck pattern must find the real health checks, or an empty offender list proves nothing");
    }
}
