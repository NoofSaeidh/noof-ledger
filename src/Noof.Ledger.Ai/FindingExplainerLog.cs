using Microsoft.Extensions.Logging;

namespace Noof.Ledger.Ai.FindingExplainerLogging;

// A sibling top-level static class in its own namespace, as ReceiptCategorizerLog is: a nested [LoggerMessage]
// class does not compile here (CS1109/CS0260).
internal static partial class FindingExplainerLog
{
    // The failure kind or the exception's type only: the exception and the model's text can quote the operator's report.
    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning, Message = "write_explanation failed ({FailureType})")]
    public static partial void ExplanationFailed(this ILogger logger, string failureType);
}
