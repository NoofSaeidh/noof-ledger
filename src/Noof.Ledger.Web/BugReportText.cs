using Noof.Ledger.Application.Diagnostics.BugReports;

namespace Noof.Ledger.Web;

// One wording of a bug report's source, status and explanation for /bugs and /bugs/{number}. Unknown is no report's
// value - bug_reports refuses 0 (spec R-1) - so it is refused here rather than printed as if it were one.
internal static class BugReportText
{
    public static string Source(BugReportSource source) => source switch
    {
        BugReportSource.Telegram => "Telegram",
        BugReportSource.Dashboard => "Dashboard",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };

    public static string Status(BugReportStatus status) => status switch
    {
        BugReportStatus.Open => "Open",
        BugReportStatus.Closed => "Closed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static string Explanation(BugExplanationState state, string? explanation) => state switch
    {
        BugExplanationState.Pending => "Not explained yet.",
        BugExplanationState.Done => explanation ?? string.Empty,
        BugExplanationState.Failed => "Couldn't explain it.",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };
}
