using Microsoft.Extensions.Logging;

namespace Noof.Ledger.Application.Diagnostics;

// A sibling top-level static class, not nested inside OperationTimer: a [LoggerMessage] extension
// method nested inside a non-static class fails to compile here with CS1109.
internal static partial class OperationTimingLog
{
    [LoggerMessage(EventId = 5301, Level = LogLevel.Debug, Message = "{Operation} took {ElapsedMs} ms")]
    public static partial void Timed(this ILogger logger, string operation, long elapsedMs);

    [LoggerMessage(EventId = 5302, Level = LogLevel.Warning,
        Message = "{Operation} took {ElapsedMs} ms, over its {ThresholdMs} ms slow threshold")]
    public static partial void Slow(this ILogger logger, string operation, long elapsedMs, long thresholdMs);
}
