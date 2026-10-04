using System.Globalization;
using Noof.Ledger.Application.Diagnostics.Integrity;
using Noof.Ledger.Application.Receipts;

namespace Noof.Ledger.Application.Diagnostics.BugReports;

// One Markdown form for a report or several: the dashboard's Download and Copy and the CLI's export render through it.
// Every free-text field loses its fiscal links and then sits inside a fence one backtick longer than any run inside it,
// so a heading or a fence the operator or a log line wrote cannot leave its section. It strips what it renders whatever
// the store already did: every free-text field loses its fiscal link before it reaches the Markdown (spec §2;
// CLAUDE.md §4 Secrets).
internal sealed class BugReportMarkdown(IFindingText findingText, IFiscalVerificationUrl verificationUrl) : IBugReportMarkdown
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
            $"- Source: {Source(report.Source)}",
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
                $"{Minute(revision.At)} UTC · {revision.ChangeKind} · {Strip(revision.Details)}")));

        yield return "### Findings when filed";
        yield return Findings(report.FindingsThen, report.SnapshotAt ?? report.CreatedAt, absent: "(not collected)");

        yield return "### Findings now";
        yield return report.TransactionId is null
            ? "(no record)"
            : Findings(report.FindingsNow, generatedAt, absent: "(not available)");

        yield return "### Not collected";
        yield return FencedOr(report.CollectionFailures, "(nothing)");

        yield return "### Explanation";
        yield return $"- State: {State(report.ExplanationState)}\n- Looks like a bug: {Verdict(report.LooksLikeBug)}";
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
        _ => Fenced(findingText.FindingsBlock([.. findings.Select(WithoutLinks)], asOf)),
    };

    // Stripped fact by fact, before FindingsBlock joins them: a link removed from the finished block would take the
    // line break after it along and merge two facts onto one line.
    IntegrityFinding WithoutLinks(IntegrityFinding finding) => finding with
    {
        Facts = [.. finding.Facts.Select(fact => fact is TextFact text ? text with { Text = Strip(text.Text) } : fact)],
    };

    string LogLine(BugReportLogLine line)
    {
        List<string> lines =
        [
            $"{line.LoggedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC "
                + $"{line.Level} {verificationUrl.StripUrl(line.Source) ?? "-"}",
            .. LinesOf(Strip(line.Message)).Select(text => $"  {text}"),
        ];

        if (line.PropertiesJson is { } properties)
            lines.Add($"  properties: {Strip(properties)}");

        if (line.Exception is { } exception)
        {
            var exceptionLines = LinesOf(Strip(exception));
            lines.Add($"  exception: {exceptionLines[0]}");
            lines.AddRange(exceptionLines.Skip(1).Select(text => $"  {text}"));
        }

        return string.Join('\n', lines);
    }

    // Unknown is no report's value - bug_reports refuses 0 (spec R-1) - so it is refused rather than printed as one.
    static string Source(BugReportSource source) => source switch
    {
        BugReportSource.Telegram => "Telegram",
        BugReportSource.Dashboard => "Dashboard",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };

    static string Status(BugReportDocument report) => report switch
    {
        { Status: BugReportStatus.Open } => "Open",
        { Status: BugReportStatus.Closed, ClosedAt: { } closedAt } => $"Closed (closed {Minute(closedAt)} UTC)",
        { Status: BugReportStatus.Closed } => "Closed",
        _ => throw new ArgumentOutOfRangeException(nameof(report), report.Status, null),
    };

    static string State(BugExplanationState state) => state switch
    {
        BugExplanationState.Pending => "Pending",
        BugExplanationState.Done => "Done",
        BugExplanationState.Failed => "Failed",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    static string Verdict(bool? looksLikeBug) => looksLikeBug switch
    {
        true => "yes",
        false => "no",
        null => "—",
    };

    string FencedOr(string? text, string absent) =>
        verificationUrl.StripUrl(text) is { } kept && !string.IsNullOrWhiteSpace(kept) ? Fenced(kept) : absent;

    string Strip(string? text) => verificationUrl.StripUrl(text) ?? "";

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
