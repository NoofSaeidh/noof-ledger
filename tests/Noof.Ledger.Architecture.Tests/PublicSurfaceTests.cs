using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

// Requirement 1 of Phase 1C: "не нужно делать публичным 'на всякий случай'". A convention cannot
// enforce that - Phase 1B shipped a README claim about [Authorize] that had already drifted. This
// is the same fix applied to accessibility: the surface is a list, and widening it is an edit to
// this file that a reviewer sees.
public class PublicSurfaceTests
{
    // Razor generates `public partial class` per .razor file with no directive to change it, and
    // EF scaffolds migration classes public and rewrites them on the next scaffold. Neither is a
    // decision anyone here gets to make, so neither is scanned.
    static readonly string[] ScannedProjects =
    [
        "Noof.Ledger.Domain",
        "Noof.Ledger.Application",
        "Noof.Ledger.Persistence",
        "Noof.Ledger.Ai",
        "Noof.Ledger.Telegram",
        "Noof.Ledger.Fx",
        "Noof.Ledger.Receipts",
        "Noof.Ledger.Host",
    ];

    static readonly Regex TopLevelPublicType = new(
        @"^public\s+(?:sealed\s+|abstract\s+|static\s+|partial\s+|readonly\s+|ref\s+)*"
        + @"(?:record\s+struct|record\s+class|class|record|interface|enum|struct)\s+"
        + @"(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    static readonly Dictionary<string, string[]> Allowed = new()
    {
        ["Noof.Ledger.Domain"] =
        [
            "AppUser", "CategorizationAuthority", "CategorizationJob", "Category", "CurrencyCode",
            "CurrencyMismatchException", "JobStatus", "LineItem", "Merchant", "MerchantAlias",
            "MerchantKind", "MerchantName", "Money", "QuotedAmount", "Transaction",
            "TransactionStatus", "Wallet",
        ],
        ["Noof.Ledger.Application"] =
        [
            "IPasswordHasher", "IUserStore", "PasswordVerifyResult", "CapturedMessage",
            "ICaptureStore", "CategoryOption", "MerchantOption", "CategorizationRequest",
            "ProposedLineItem", "CategorizationProposal", "ResolvedLineItem", "CategorizedLineItem",
            "CategorizationSubject", "CategoryEntry", "MerchantAliasEntry", "ICategorizationStore",
            "ICategorizer", "ICategoryCatalog", "IMerchantDirectory", "MerchantScan",
            "ModelFailureKind", "ModelCallException", "ModelCallExceptionExtensions",
            "ProposalVerification", "IChatNotifier", "IJobQueue", "JobCompletionOutcome",
            "RecentLineItem", "RecentTransaction", "MonthTotal", "MonthSummary",
            "ISpendingReadModel", "ProbeResult", "ISecretProbe", "ISecretStore", "SecretKeys",
            "SecretResult", "SecretState", "SecretStatus",
        ],
        ["Noof.Ledger.Persistence"] = ["LedgerConnectionString", "PersistenceRegistration"],
        ["Noof.Ledger.Ai"] = ["AiRegistration"],
        ["Noof.Ledger.Telegram"] = ["TelegramRegistration"],
        ["Noof.Ledger.Fx"] = [],
        ["Noof.Ledger.Receipts"] = [],
        ["Noof.Ledger.Host"] =
        [
            "AuthSchemes", "LocalOwnerHandler", "PasswordHasherAdapter", "UserCommand",
            "AccountEndpoints", "Program", "CaptureTimeZoneGuard", "DataProtectionSetup",
            "LoopbackGuard", "CategorizationReply", "CategorizationTickResult",
            "CategorizationWorker", "CategorizationWorkerOptions",
        ],
    };

    [Theory]
    [MemberData(nameof(Projects))]
    public void Public_types_are_exactly_the_allowed_set(string project)
    {
        PublicTypesIn(project).Should().BeEquivalentTo(Allowed[project],
            $"the public surface of {project} is a reviewed list, not whatever accumulated; widening "
            + "it means editing PublicSurfaceTests.Allowed, which is the point");
    }

    [Fact]
    public void No_public_concrete_service_crosses_an_infrastructure_boundary()
    {
        string[] infrastructure = ["Noof.Ledger.Persistence", "Noof.Ledger.Ai", "Noof.Ledger.Telegram"];

        var offenders = infrastructure
            .SelectMany(project => SourceFiles(project)
                .SelectMany(file => ConcreteServiceDeclarations(File.ReadAllText(file))
                    .Select(name => $"{project}/{name}")))
            .ToArray();

        // Requirement 2: a service that crosses an assembly boundary is reached through an
        // interface. The teeth are here rather than in a naming convention - if the implementation
        // is internal, the interface is the only way in, and this test is what notices when one
        // stops being internal.
        offenders.Should().BeEmpty(
            "an implementation that another assembly can name by type is an implementation another "
            + "assembly can depend on; Application owns the interface, the infrastructure owns the class");
    }

    [Fact]
    public void Every_scanned_project_exists_and_the_domain_surface_is_not_empty()
    {
        foreach (var project in ScannedProjects)
            Directory.Exists(ProjectRoot(project)).Should().BeTrue($"{project} must exist to be scanned");

        // Fx and Receipts are legitimately empty placeholders, so "every list is non-empty" would be
        // wrong. Domain standing in for the set proves the regex still matches real declarations - a
        // rule whose subject set is empty passes forever and enforces nothing.
        PublicTypesIn("Noof.Ledger.Domain").Should().NotBeEmpty();
    }

    public static TheoryData<string> Projects()
    {
        TheoryData<string> data = [];
        foreach (var project in ScannedProjects)
            data.Add(project);

        return data;
    }

    static string ProjectRoot(string project) =>
        Path.Combine(RepoRoot.Find().FullName, "src", project);

    static IEnumerable<string> SourceFiles(string project) =>
        Directory.Exists(ProjectRoot(project))
            ? Directory.EnumerateFiles(ProjectRoot(project), "*.cs", SearchOption.AllDirectories)
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            : [];

    static string[] PublicTypesIn(string project) =>
        [.. SourceFiles(project)
            .SelectMany(file => TopLevelPublicType.Matches(File.ReadAllText(file)))
            .Select(match => match.Groups["name"].Value)];

    // A "concrete service" is a public non-static, non-abstract class. Records, enums, structs and
    // static classes are data and helpers - requirement 2 exempts "примитивные экстешены или хелперы".
    static IEnumerable<string> ConcreteServiceDeclarations(string source) =>
        TopLevelPublicType.Matches(source)
            .Where(match => match.Value.Contains(" class ", StringComparison.Ordinal))
            .Where(match => !match.Value.Contains(" static ", StringComparison.Ordinal))
            .Where(match => !match.Value.Contains(" abstract ", StringComparison.Ordinal))
            .Where(match => !match.Value.Contains(" record ", StringComparison.Ordinal))
            .Select(match => match.Groups["name"].Value);
}
