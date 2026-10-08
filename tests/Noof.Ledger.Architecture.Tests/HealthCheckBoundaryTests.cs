using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

// Scope item for Phase 5 Task 6: the LLM plays no part in health. AiKeysHealthCheck reads
// IModelProvider/ISpeechProvider - both Application interfaces - never Noof.Ledger.Ai's own model
// or speech types directly. Phase 8a (spec §1 "Boundary"): the integrity checks feed the health tile,
// so they are held to the same rule, and neither may name the finding explainer - nor, since Phase 8b,
// the summary explainer, so the exchange-rates check cannot reach the model either. Persistence cannot
// reference the Ai assembly, so naming an Application interface that Ai implements with a model call
// is the only way a check could reach one - those interfaces are all listed. The whole Integrity
// folder is scanned, so a helper the run executes, or a check that derives from a base class, cannot
// slip past the declaration pattern.
public class HealthCheckBoundaryTests
{
    static readonly string SrcRoot = Path.Combine(RepoRoot.Find().FullName, "src");
    static readonly Regex HealthCheckDeclaration = new(@"[:,]\s*ISystemHealthCheck\b", RegexOptions.Compiled);
    static readonly Regex IntegrityCheckDeclaration = new(@"[:,]\s*IIntegrityChecks?\b", RegexOptions.Compiled);

    static readonly string IntegrityFolder =
        Path.Combine("Noof.Ledger.Persistence", "Diagnostics", "Integrity") + Path.DirectorySeparatorChar;

    static readonly string[] ModelStack =
    [
        "Microsoft.Extensions.AI", "IChatClient", "ISpeechToTextClient", "ICategorizer", "ITranscriber",
        "IReceiptVision", "IReceiptCategorizer", "IFindingExplainer", "ISummaryExplainer",
    ];

    [Fact]
    public void No_health_check_reaches_a_model()
    {
        var sources = Directory.EnumerateFiles(SrcRoot, "*.cs", SearchOption.AllDirectories)
            .Select(file => (Path: Path.GetRelativePath(SrcRoot, file), Text: File.ReadAllText(file)))
            .ToArray();
        var healthCheckFiles = sources.Where(source => HealthCheckDeclaration.IsMatch(source.Text)).ToArray();
        var integrityFolderFiles = sources
            .Where(source => source.Path.StartsWith(IntegrityFolder, StringComparison.Ordinal))
            .ToArray();
        var integrityFiles = sources.Where(source => IntegrityCheckDeclaration.IsMatch(source.Text)).ToArray();

        var offenders = healthCheckFiles.Concat(integrityFolderFiles).Concat(integrityFiles)
            .Where(source => ModelStack.Any(name => source.Text.Contains(name, StringComparison.Ordinal)))
            .Select(source => source.Path)
            .Distinct()
            .ToArray();

        offenders.Should().BeEmpty(
            "health and integrity checks read IModelProvider/ISpeechProvider at most, never the model or speech client "
            + "stack or an explainer");
        healthCheckFiles.Should().NotBeEmpty(
            "the ISystemHealthCheck pattern must find the real health checks, or an empty offender list proves nothing");
        healthCheckFiles.Select(source => source.Path)
            .Should().Contain(file => file.StartsWith("Noof.Ledger.Ai" + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                "AiKeysHealthCheck must still be found under src/Noof.Ledger.Ai/");
        integrityFolderFiles.Should().NotBeEmpty(
            "src/Noof.Ledger.Persistence/Diagnostics/Integrity/ must hold the integrity checks, or an empty offender "
            + "list proves nothing for them");
        integrityFiles.Select(source => source.Path)
            .Should().Contain(file => file.StartsWith(IntegrityFolder, StringComparison.Ordinal),
                "the integrity pattern must find the checks under src/Noof.Ledger.Persistence/Diagnostics/Integrity/, "
                + "or an empty offender list proves nothing for them");
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
