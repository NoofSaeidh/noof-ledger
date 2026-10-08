namespace Noof.Ledger.Host.Workers.FxRateLogging;

// A sibling top-level static class in its own namespace, as BugReportExplanationWorkerLog is: TickFailed must never be
// one of two visible extension-method candidates at a call site (CS0121).
internal static partial class FxRateWorkerLog
{
    [LoggerMessage(EventId = 2301, Level = LogLevel.Error, Message = "Exchange rate worker tick failed")]
    public static partial void TickFailed(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2302, Level = LogLevel.Information,
        Message = "Exchange rates of {AsOfDate} stored for {Currencies} currencies")]
    public static partial void RatesStored(this ILogger logger, DateOnly asOfDate, int currencies);

    [LoggerMessage(EventId = 2303, Level = LogLevel.Information,
        Message = "Exchange rates of {AsOfDate} were already stored; the source has published nothing newer")]
    public static partial void RatesAlreadyStored(this ILogger logger, DateOnly asOfDate);

    // The source warned with the reason under Noof.Ledger.Fx; this line is what the health row's Logs link finds.
    [LoggerMessage(EventId = 2304, Level = LogLevel.Information,
        Message = "No exchange rates fetched this tick; the reason is logged under Noof.Ledger.Fx")]
    public static partial void NothingFetched(this ILogger logger);
}
