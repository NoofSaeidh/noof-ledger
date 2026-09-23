using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

// Operator decision D-A (2026-09-23): nothing depends on a model or speech provider except its factory, and
// everything provider-specific lives in that provider's folder.
public class AiBoundaryTests
{
    static readonly string SrcRoot = Path.Combine(RepoRoot.Find().FullName, "src");
    static readonly string AiRoot = Path.Combine(SrcRoot, "Noof.Ledger.Ai") + Path.DirectorySeparatorChar;
    static readonly string ProviderFolder = Path.Combine(SrcRoot, "Noof.Ledger.Ai", "Anthropic") + Path.DirectorySeparatorChar;

    // A using directive for the SDK's namespaces (plain, static, aliased or global::), or a name
    // qualified with them. Not Noof.Ledger.Ai.Anthropic: that is this repository's folder namespace.
    static readonly Regex SdkReference = new(
        @"^\s*(?:global\s+)?using\s+(?:static\s+)?(?:\w+\s*=\s*)?(?:global::)?Anthropic\b|(?<![\w.])Anthropic\.\w",
        RegexOptions.Multiline | RegexOptions.Compiled);

    [Fact]
    public void Only_the_provider_folder_references_the_Anthropic_SDK()
    {
        // A project that references Noof.Ledger.Ai inherits its PackageReference to Anthropic
        // transitively at compile time, so a project reference alone stops nothing - not Host,
        // and not the provider-neutral half of Noof.Ledger.Ai itself.
        var offenders = SourceFiles("*.cs")
            .Where(file => !InProviderFolder(file))
            .Where(file => SdkReference.IsMatch(File.ReadAllText(file)))
            .Select(Relative)
            .ToArray();

        offenders.Should().BeEmpty("the SDK is reached through IChatClientFactory, never directly");
        SourceFiles("*.cs").Where(InProviderFolder).Should().Contain(file => SdkReference.IsMatch(File.ReadAllText(file)),
            "the provider folder must use the SDK, or an empty offender list proves nothing about the pattern");
    }

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("Groq")]
    public void No_type_outside_its_provider_folder_is_named_after_the_provider(string provider)
    {
        var folder = Path.Combine(SrcRoot, "Noof.Ledger.Ai", provider) + Path.DirectorySeparatorChar;
        var namedAfterProvider = new Regex(@$"\b(?:class|record|interface|enum|struct)\s+(?<name>\w*{provider}\w*)");

        var offenders = SourceFiles("*.cs")
            .Where(file => !file.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            .SelectMany(file => namedAfterProvider.Matches(File.ReadAllText(file))
                .Select(match => $"{Relative(file)}: {match.Groups["name"].Value}"))
            .ToArray();

        offenders.Should().BeEmpty("a type named after the provider belongs with the provider");
        SourceFiles("*.cs").Where(file => file.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            .Should().Contain(file => namedAfterProvider.IsMatch(File.ReadAllText(file)),
                "the provider folder's own types must match, or the pattern is matching nothing");
    }

    [Theory]
    [InlineData("anthropic")]
    [InlineData("groq")]
    public void Nothing_outside_the_Ai_assembly_names_the_provider(string provider)
    {
        // Host and Web learn what they need - which secret to ask for, what to call it, whether it is set -
        // through IModelProvider and ISpeechProvider. Case-insensitive, so a stored key's own spelling
        // ("anthropic-api-key", "groq-api-key") counts too: it belongs to the provider, not to Application.
        var offenders = SourceFiles("*.cs").Concat(SourceFiles("*.razor"))
            .Where(file => !file.StartsWith(AiRoot, StringComparison.OrdinalIgnoreCase))
            .Where(file => File.ReadAllText(file).Contains(provider, StringComparison.OrdinalIgnoreCase))
            .Select(Relative)
            .ToArray();

        offenders.Should().BeEmpty("only Noof.Ledger.Ai may know which provider answers");
    }

    [Fact]
    public void Only_the_Groq_folder_knows_where_Groq_is()
    {
        var groqFolder = Path.Combine(SrcRoot, "Noof.Ledger.Ai", "Groq") + Path.DirectorySeparatorChar;
        var naming = SourceFiles("*.cs").Where(file => File.ReadAllText(file).Contains("api.groq.com", StringComparison.OrdinalIgnoreCase)).ToArray();

        naming.Where(file => !file.StartsWith(groqFolder, StringComparison.OrdinalIgnoreCase)).Select(Relative)
            .Should().BeEmpty("the endpoint is the provider's own detail");
        naming.Should().NotBeEmpty("the Groq folder must name its endpoint, or the rule proves nothing");
    }

    [Fact]
    public void Only_ProposalMapper_constructs_a_Money_inside_the_categorization_pipeline()
    {
        var root = RepoRoot.Find().FullName;
        var mapper = Path.Combine(root, "src", "Noof.Ledger.Application", "Categorization", "ProposalMapper.cs");
        string[] scannedRoots =
        [
            Path.Combine(root, "src", "Noof.Ledger.Ai"),
            Path.Combine(root, "src", "Noof.Ledger.Application", "Categorization"),
            Path.Combine(root, "src", "Noof.Ledger.Host", "Workers"),
        ];

        var offenders = scannedRoots
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            .Where(file => !string.Equals(file, mapper, StringComparison.OrdinalIgnoreCase))
            .Where(file => File.ReadAllText(file).Contains("new Money(", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(root, file))
            .ToArray();

        // One door, not a check: the model's reading of an amount becomes a Money in exactly one place,
        // so a wrong figure has exactly one place to be traced to. The verbatim check that used to live
        // here was removed on purpose (decision D1).
        offenders.Should().BeEmpty("a Money built from a model answer must come from ProposalMapper");
        File.ReadAllText(mapper).Should().Contain("new Money(",
            "the one permitted site must exist, or an empty offender list proves nothing");
    }

    static IEnumerable<string> SourceFiles(string pattern) =>
        Directory.EnumerateFiles(SrcRoot, pattern, SearchOption.AllDirectories);

    static bool InProviderFolder(string file) => file.StartsWith(ProviderFolder, StringComparison.OrdinalIgnoreCase);

    static string Relative(string file) => Path.GetRelativePath(SrcRoot, file);
}
