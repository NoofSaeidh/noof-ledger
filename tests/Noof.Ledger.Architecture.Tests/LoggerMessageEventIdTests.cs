using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

// Task V1 (Phase 5 verbose logging): EventIds are pinned and unique across the whole solution, and
// none of them collide with a TransactionStages id - the trace reader keys off those. A source-text
// scan, like LoggingBoundaryTests, because the rule must hold before the compiler ever runs.
public class LoggerMessageEventIdTests
{
    static readonly string SrcRoot = Path.Combine(RepoRoot.Find().FullName, "src");
    static readonly string TransactionStagesFile = Path.Combine(
        SrcRoot, "Noof.Ledger.Application", "Diagnostics", "TransactionStages.cs");

    static readonly Regex LoggerMessageAttribute = new(
        @"\[LoggerMessage\((?<args>.*?)\)\]",
        RegexOptions.Singleline | RegexOptions.Compiled);

    static readonly Regex LiteralEventId = new(
        @"EventId\s*=\s*(?<id>\d+)",
        RegexOptions.Compiled);

    static readonly Regex StageIdConstant = new(
        @"const\s+int\s+\w+EventId\s*=\s*(?<id>\d+)",
        RegexOptions.Compiled);

    [Fact]
    public void Every_LoggerMessage_declares_an_EventId()
    {
        var offenders = new List<string>();

        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);
            var lineStarts = LineStarts(text);

            foreach (Match match in LoggerMessageAttribute.Matches(text))
            {
                var args = match.Groups["args"].Value;
                if (!args.Contains("EventId =", StringComparison.Ordinal))
                    offenders.Add($"{Relative(file)}:{LineNumber(lineStarts, match.Index)}");
            }
        }

        offenders.Should().BeEmpty("every [LoggerMessage] must declare a pinned EventId");
    }

    [Fact]
    public void Literal_EventIds_are_unique()
    {
        var occurrences = new List<(int Id, string Location)>();

        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);
            var lineStarts = LineStarts(text);

            foreach (Match attribute in LoggerMessageAttribute.Matches(text))
            {
                var idMatch = LiteralEventId.Match(attribute.Groups["args"].Value);
                if (!idMatch.Success)
                    continue;

                var id = int.Parse(idMatch.Groups["id"].Value);
                occurrences.Add((id, $"{Relative(file)}:{LineNumber(lineStarts, attribute.Index)}"));
            }
        }

        var duplicates = occurrences
            .GroupBy(o => o.Id)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} at [{string.Join(", ", g.Select(o => o.Location))}]")
            .ToArray();

        duplicates.Should().BeEmpty("every literal EventId must be unique across the solution");
    }

    [Fact]
    public void No_literal_EventId_reuses_a_TransactionStages_id()
    {
        var stageIds = StageEventIds();

        var offenders = new List<string>();

        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);
            var lineStarts = LineStarts(text);

            foreach (Match attribute in LoggerMessageAttribute.Matches(text))
            {
                var args = attribute.Groups["args"].Value;
                if (args.Contains("TransactionStages.", StringComparison.Ordinal))
                    continue;

                var idMatch = LiteralEventId.Match(args);
                if (!idMatch.Success)
                    continue;

                var id = int.Parse(idMatch.Groups["id"].Value);
                if (stageIds.Contains(id))
                    offenders.Add($"{Relative(file)}:{LineNumber(lineStarts, attribute.Index)} reuses TransactionStages id {id}");
            }
        }

        offenders.Should().BeEmpty("a literal EventId must never reuse a TransactionStages id");
    }

    [Fact]
    public void Both_subject_sets_are_non_empty()
    {
        var literalCount = SourceFiles()
            .SelectMany(file => LoggerMessageAttribute.Matches(File.ReadAllText(file)).Cast<Match>())
            .Count(m => LiteralEventId.IsMatch(m.Groups["args"].Value));

        literalCount.Should().BeGreaterThan(0, "the scan would enforce nothing over an empty subject set");
        StageEventIds().Should().NotBeEmpty("TransactionStages must expose at least one id for the exemption to mean anything");
    }

    static HashSet<int> StageEventIds() =>
        [.. StageIdConstant.Matches(File.ReadAllText(TransactionStagesFile))
            .Select(m => int.Parse(m.Groups["id"].Value))];

    static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(SrcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    static string Relative(string file) => Path.GetRelativePath(SrcRoot, file);

    static int[] LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n')
                starts.Add(i + 1);

        return [.. starts];
    }

    static int LineNumber(int[] lineStarts, int index)
    {
        var line = Array.BinarySearch(lineStarts, index);
        return (line >= 0 ? line : ~line - 1) + 1;
    }
}
