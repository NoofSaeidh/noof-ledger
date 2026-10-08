using Microsoft.Extensions.Logging;

namespace Noof.Ledger.Ai.SummaryExplainerLogging;

// A sibling top-level static class in its own namespace, as FindingExplainerLog is: a nested [LoggerMessage] class does
// not compile here (CS1109/CS0260).
internal static partial class SummaryExplainerLog
{
    // The failure kind or the exception's type only: the exception and the model's text can quote the summary.
    [LoggerMessage(EventId = 2101, Level = LogLevel.Warning, Message = "write_summary_comment failed ({FailureType})")]
    public static partial void SummaryCommentFailed(this ILogger logger, string failureType);
}
