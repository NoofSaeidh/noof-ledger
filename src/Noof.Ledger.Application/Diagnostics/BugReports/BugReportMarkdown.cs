using System.Globalization;
using Noof.Ledger.Application.Diagnostics.Integrity;

namespace Noof.Ledger.Application.Diagnostics.BugReports;

// One Markdown form for a report or several: the dashboard's Download and Copy and the CLI's export render through it.
// Every free-text field sits inside a fence one backtick longer than any run inside it, so a heading or a fence the
// operator or a log line wrote cannot leave its section.
internal sealed class BugReportMarkdown(IFindingText findingText) : IBugReportMarkdown
{
    const string PrivacyWarning =
        "> Contains the operator's own financial data. Never paste it into a public issue, commit, PR or backlog entry.";

    public string Render(IReadOnlyList<BugReportDocument> reports, DateTimeOffset generatedAt)
    {
        List<string> blocks =
        [
            "# noof-ledger bug reports",
            $"Exported {Minute(generatedAt)} UTC · {(reports.Count == 1 ? "1 report" : $"{reports.Count} reports")}",
            PrivacyWarning,
            .. reports.OrderBy(report => report.Number).SelectMany(report => Blocks(report, generatedAt)),
        ];

        return string.Join("\n\n", blocks) + "\n";
    }

    IEnumerable<string> Blocks(BugReportDocument report, DateTimeOffset generatedAt)
    {
        yield return $"## Bug report #{report.Number}";
        yield return string.Join('\n',
            $"- Filed: {Minute(report.CreatedAt)} UTC",
            $"- Source: {report.Source}",
            $"- Status: {Status(report)}",
            $"- Record: {report.TransactionId?.ToString() ?? "none"}",
            $"- Snapshot: {(report.SnapshotAt is { } takenAt ? $"{Minute(takenAt)} UTC" : "not taken yet")}");

        yield return "### Operator's text";
        yield return FencedOr(report.Text, "(none)");

        yield return "### Record as filed";
        yield return FencedOr(report.RecordSummary, "(none)");

        yield return "### Revision history";
        yield return report.Revisions.Count == 0
            ? "(none)"
            : Fenced(string.Join('\n', report.Revisions.Select(revision =>
                $"{Minute(revision.At)} UTC · {revision.ChangeKind} · {revision.Details}")));

        yield return "### Findings when filed";
        yield return Findings(report.FindingsThen, report.SnapshotAt ?? report.CreatedAt, absent: "(not collected)");

        yield return "### Findings now";
        yield return report.TransactionId is null
            ? "(no record)"
            : Findings(report.FindingsNow, generatedAt, absent: "(not available)");

        yield return "### Not collected";
        yield return FencedOr(report.CollectionFailures, "(nothing)");

        yield return "### Explanation";
        yield return $"- State: {report.ExplanationState}\n- Looks like a bug: {Verdict(report.LooksLikeBug)}";
        yield return FencedOr(report.Explanation, "(none)");

        yield return "### Log lines (newest first)";
        yield return report.LogLines switch
        {
            null => "(not collected)",
            [] => "(none)",
            var lines => Fenced(string.Join('\n', lines.Select(LogLine))),
        };
    }

    string Findings(IReadOnlyList<IntegrityFinding>? findings, DateTimeOffset asOf, string absent) => findings switch
    {
        null => absent,
        [] => "(none)",
        _ => Fenced(findingText.FindingsBlock(findings, asOf)),
    };

    static string LogLine(BugReportLogLine line)
    {
        List<string> lines =
        [
            $"{line.LoggedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC "
                + $"{line.Level} {line.Source ?? "-"}",
            .. LinesOf(line.Message).Select(text => $"  {text}"),
        ];

        if (line.PropertiesJson is { } properties)
            lines.Add($"  properties: {properties}");

        if (line.Exception is { } exception)
        {
            var exceptionLines = LinesOf(exception);
            lines.Add($"  exception: {exceptionLines[0]}");
            lines.AddRange(exceptionLines.Skip(1).Select(text => $"  {text}"));
        }

        return string.Join('\n', lines);
    }

    static string Status(BugReportDocument report) => report is { Status: BugReportStatus.Closed, ClosedAt: { } closedAt }
        ? $"Closed (closed {Minute(closedAt)} UTC)"
        : report.Status.ToString();

    static string Verdict(bool? looksLikeBug) => looksLikeBug switch
    {
        true => "yes",
        false => "no",
        null => "—",
    };

    static string FencedOr(string? text, string absent) => string.IsNullOrWhiteSpace(text) ? absent : Fenced(text);

    static string Fenced(string text)
    {
        var body = text.ReplaceLineEndings("\n");
        var fence = new string('`', Math.Max(3, LongestBacktickRun(body) + 1));
        return $"{fence}text\n{body}\n{fence}";
    }

    static int LongestBacktickRun(string text)
    {
        var longest = 0;
        var run = 0;
        foreach (var character in text)
        {
            run = character == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        return longest;
    }

    static string[] LinesOf(string text) => text.ReplaceLineEndings("\n").Split('\n');

    static string Minute(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
