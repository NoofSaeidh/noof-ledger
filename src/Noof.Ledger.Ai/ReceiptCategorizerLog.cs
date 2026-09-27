using Microsoft.Extensions.Logging;

namespace Noof.Ledger.Ai.ReceiptCategorizerLogging;

// A sibling top-level static class, not nested inside ChatReceiptCategorizer - CLAUDE.md's
// [LoggerMessage] rule (a nested Log class does not compile here, CS1109/CS0260). In its own
// namespace so ChatReceiptCategorizer.cs can import it without also importing every other worker's
// LoggerMessage extension methods.
internal static partial class ReceiptCategorizerLog
{
    [LoggerMessage(EventId = 1501, Level = LogLevel.Warning,
        Message = "categorize_receipt did not answer ordinal {Ordinal}; falling back to \"{FallbackSlug}\"")]
    public static partial void MissingOrdinal(this ILogger logger, int ordinal, string fallbackSlug);

    [LoggerMessage(EventId = 1502, Level = LogLevel.Warning,
        Message = "categorize_receipt answered ordinal {Ordinal}, which was not one of the lines offered; ignored")]
    public static partial void ExtraOrdinalIgnored(this ILogger logger, int ordinal);
}
