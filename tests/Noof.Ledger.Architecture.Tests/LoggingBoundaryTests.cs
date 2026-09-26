using System.Text.RegularExpressions;
using System.Xml.Linq;
using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

// Task 2 (Phase 5 observability): Serilog is only the provider behind Microsoft.Extensions.Logging,
// and every call site goes through a source-generated [LoggerMessage] method - never a direct
// logger.LogXxx(...) call. Both rules are enforced here because a convention alone did not hold for
// the Anthropic SDK boundary either (see AiBoundaryTests) and there is no reason logging would be
// different.
public class LoggingBoundaryTests
{
    static readonly string SrcRoot = Path.Combine(RepoRoot.Find().FullName, "src");
    static readonly string HostRoot = Path.Combine(SrcRoot, "Noof.Ledger.Host") + Path.DirectorySeparatorChar;

    static readonly Regex SerilogReference = new(
        @"^\s*(?:global\s+)?using\s+(?:static\s+)?(?:\w+\s*=\s*)?(?:global::)?Serilog\b|(?<![\w.])Serilog\.\w",
        RegexOptions.Multiline | RegexOptions.Compiled);

    static readonly Regex DirectLoggerCall = new(
        @"\.Log(?:Trace|Debug|Information|Warning|Error|Critical)\s*\(",
        RegexOptions.Compiled);

    static readonly Regex EventIdAssignment = new(
        @"\bEventId\s*=\s*(?<value>TransactionStages\.\w+|\d+)",
        RegexOptions.Compiled);

    static readonly Regex TransactionStageConst = new(
        @"public\s+const\s+int\s+(?<name>\w+)\s*=\s*(?<value>\d+)\s*;",
        RegexOptions.Compiled);

    [Fact]
    public void Only_Noof_Ledger_Host_references_Serilog()
    {
        var offenders = SourceFiles("*.cs")
            .Where(file => !InHost(file))
            .Where(file => SerilogReference.IsMatch(File.ReadAllText(file)))
            .Select(Relative)
            .ToArray();

        offenders.Should().BeEmpty("Serilog is reached through ILogger<T>; only the Host composition root may name it");
        SourceFiles("*.cs").Where(InHost).Should().Contain(file => SerilogReference.IsMatch(File.ReadAllText(file)),
            "Host itself must use Serilog, or an empty offender list proves nothing about the pattern");
    }

    [Fact]
    public void Only_Noof_Ledger_Host_has_a_Serilog_package_reference()
    {
        var projectDirs = Directory.EnumerateDirectories(SrcRoot);

        var offenders = projectDirs
            .Select(dir => (dir, csproj: Directory.EnumerateFiles(dir, "*.csproj").SingleOrDefault()))
            .Where(entry => entry.csproj is not null)
            .Where(entry => !string.Equals(Path.GetFileNameWithoutExtension(entry.csproj), "Noof.Ledger.Host", StringComparison.Ordinal))
            .Where(entry => XDocument.Load(entry.csproj!).Descendants("PackageReference")
                .Any(IsSerilogPackage))
            .Select(entry => Path.GetFileName(entry.dir))
            .ToArray();

        offenders.Should().BeEmpty("Serilog packages are referenced by Noof.Ledger.Host only");

        var hostCsproj = Path.Combine(HostRoot.TrimEnd(Path.DirectorySeparatorChar), "Noof.Ledger.Host.csproj");
        XDocument.Load(hostCsproj).Descendants("PackageReference")
            .Should().Contain(e => IsSerilogPackage(e),
                "Host itself must reference Serilog, or an empty offender list proves nothing");
    }

    [Fact]
    public void No_source_file_calls_a_LogXxx_convenience_method_directly()
    {
        var offenders = SourceFiles("*.cs")
            .Where(file => DirectLoggerCall.IsMatch(File.ReadAllText(file)))
            .Select(Relative)
            .ToArray();

        offenders.Should().BeEmpty(
            "every log call goes through a source-generated [LoggerMessage] method, never logger.LogXxx(...) directly");
    }

    // M-2 (Phase 6 final review): before Phase 6, every [LoggerMessage] EventId was unique. The
    // TransactionStages ids (5001-5010) are the one deliberate exception - the same trace stage is
    // written by more than one worker (Categorized by both CategorizationWorker and
    // ReceiptCategorizationWorker, for instance), and EfTransactionTrace reads those rows back by
    // id, so sharing is the point, not a bug. A plain numeric EventId reused anywhere else means the
    // same wire id now names two unrelated events, which makes filtering /diagnostics/logs by id
    // ambiguous - that already happened twice in Phase 6 (ExtractReceiptWorkerLog vs
    // ReceiptCategorizerLog, ReceiptCategorizationWorkerLog vs TranscriptionWorkerLog).
    [Fact]
    public void Every_LoggerMessage_EventId_is_unique_outside_the_shared_trace_stages()
    {
        var stageIds = TransactionStageEventIds();

        var usages = SourceFiles("*.cs")
            .SelectMany(file => LoggerMessageEventIds(file, stageIds))
            .ToArray();

        var collisions = usages
            .GroupBy(usage => usage.Value)
            .Where(group => group.Count() > 1 && !group.All(usage => usage.IsTransactionStage))
            .Select(group => $"EventId {group.Key}: {string.Join(", ", group.Select(usage => $"{usage.File}:{usage.Line}"))}")
            .ToArray();

        collisions.Should().BeEmpty(
            "every [LoggerMessage] EventId must be unique, except the TransactionStages ids (5001-5010) " +
            "that multiple stage writers deliberately share");
        usages.Should().HaveCountGreaterThan(1, "the scan must actually find [LoggerMessage] attributes, or this proves nothing");
    }

    static Dictionary<string, int> TransactionStageEventIds()
    {
        var file = Path.Combine(
            RepoRoot.Find().FullName, "src", "Noof.Ledger.Application", "Diagnostics", "TransactionStages.cs");

        return TransactionStageConst.Matches(File.ReadAllText(file))
            .ToDictionary(match => match.Groups["name"].Value, match => int.Parse(match.Groups["value"].Value));
    }

    static IEnumerable<EventIdUsage> LoggerMessageEventIds(string file, IReadOnlyDictionary<string, int> stageIds)
    {
        var text = File.ReadAllText(file);
        foreach (Match match in EventIdAssignment.Matches(text))
        {
            var raw = match.Groups["value"].Value;
            var isStage = raw.StartsWith("TransactionStages.", StringComparison.Ordinal);
            var value = isStage ? stageIds[raw["TransactionStages.".Length..]] : int.Parse(raw);
            var line = text[..match.Index].Count(c => c == '\n') + 1;
            yield return new EventIdUsage(Relative(file), line, value, isStage);
        }
    }

    readonly record struct EventIdUsage(string File, int Line, int Value, bool IsTransactionStage);

    static IEnumerable<string> SourceFiles(string pattern) =>
        Directory.EnumerateFiles(SrcRoot, pattern, SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    static bool InHost(string file) => file.StartsWith(HostRoot, StringComparison.OrdinalIgnoreCase);

    static string Relative(string file) => Path.GetRelativePath(SrcRoot, file);

    static bool IsSerilogPackage(XElement packageReference) =>
        (packageReference.Attribute("Include")?.Value ?? "").StartsWith("Serilog", StringComparison.Ordinal);
}
