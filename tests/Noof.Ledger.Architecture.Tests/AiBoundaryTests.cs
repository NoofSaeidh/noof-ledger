using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

public class AiBoundaryTests
{
    [Fact]
    public void Only_Ai_references_the_Anthropic_SDK_namespace()
    {
        var srcRoot = Path.Combine(RepoRoot.Find().FullName, "src");
        var aiRoot = Path.Combine(srcRoot, "Noof.Ledger.Ai") + Path.DirectorySeparatorChar;

        var offenders = Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.StartsWith(aiRoot, StringComparison.OrdinalIgnoreCase))
            .Where(file => ReferencesAnthropicSdk(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(srcRoot, file))
            .ToArray();

        // A project that references Noof.Ledger.Ai inherits its PackageReference to Anthropic
        // transitively at compile time, so "Web doesn't reference Ai" alone does not stop Host -
        // which legitimately references Ai to run the worker - from calling the SDK directly
        // instead of through ICategorizer. This is the rule that actually closes that gap.
        offenders.Should().BeEmpty(
            "every project outside Noof.Ledger.Ai must reach the model through ICategorizer, never the Anthropic SDK directly");
    }

    [Fact]
    public void Only_QuotedAmount_constructs_a_Money_inside_the_categorization_pipeline()
    {
        var root = RepoRoot.Find().FullName;
        string[] scannedRoots =
        [
            Path.Combine(root, "src", "Noof.Ledger.Ai"),
            Path.Combine(root, "src", "Noof.Ledger.Host", "Workers"),
        ];

        var offenders = scannedRoots
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            .Where(file => File.ReadAllText(file).Contains("new Money(", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(root, file))
            .ToArray();

        // ResolvedLineItem.Amount and CategorizedLineItem.Amount both carry forward the Money that
        // QuotedAmount.TryResolve (Noof.Ledger.Domain) already verified against the raw text. The
        // categorization pipeline must never mint a second one - that would be a figure reaching a
        // line item without ever being checked against what the user actually typed.
        offenders.Should().BeEmpty(
            "a Money in the categorization pipeline must come from QuotedAmount.TryResolve, never be constructed fresh");
    }

    static bool ReferencesAnthropicSdk(string source) =>
        source.Contains("using Anthropic;", StringComparison.Ordinal) ||
        source.Contains("Anthropic.", StringComparison.Ordinal);
}
