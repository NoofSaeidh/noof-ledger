using Microsoft.Extensions.Logging;

namespace Noof.Ledger.Fx;

// A sibling top-level static class: a [LoggerMessage] extension nested in a non-static class does not compile here
// (CS1109). Fixed phrases only, never an exception's message.
internal static partial class OpenErApiRateSourceLog
{
    [LoggerMessage(EventId = 2201, Level = LogLevel.Warning, Message = "Exchange rates not fetched from open.er-api.com: {Reason}")]
    public static partial void RatesNotFetched(this ILogger logger, string reason);

    [LoggerMessage(EventId = 2202, Level = LogLevel.Warning,
        Message = "Exchange rates from open.er-api.com rejected, nothing stored: {Reason}")]
    public static partial void RatesRejected(this ILogger logger, string reason);

    [LoggerMessage(EventId = 2203, Level = LogLevel.Warning,
        Message = "open.er-api.com announced the end of its free endpoint (time_eol_unix {EndOfLifeUnix}); rates will stop arriving")]
    public static partial void EndOfLifeAnnounced(this ILogger logger, long endOfLifeUnix);
}
