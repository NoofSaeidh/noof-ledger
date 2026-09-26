using Microsoft.Extensions.Logging;

namespace Noof.Ledger.Application.Diagnostics;

internal sealed class OperationTimer(TimeProvider clock, SlowOperationOptions options) : IOperationTimer
{
    public OperationTiming Start(ILogger logger, string operation, TimeSpan expectedWait = default) =>
        new(this, clock, logger, operation, expectedWait);

    public void Record(ILogger logger, string operation, TimeSpan elapsed, bool onlyIfSlow = false, TimeSpan expectedWait = default) =>
        LogOutcome(logger, operation, elapsed, onlyIfSlow, expectedWait);

    internal void LogOutcome(ILogger logger, string operation, TimeSpan elapsed, bool onlyIfSlow, TimeSpan expectedWait)
    {
        var elapsedMs = (long)elapsed.TotalMilliseconds;
        var thresholdMs = Threshold(operation) + (long)expectedWait.TotalMilliseconds;

        if (elapsedMs >= thresholdMs)
            logger.Slow(operation, elapsedMs, thresholdMs);
        else if (!onlyIfSlow)
            logger.Timed(operation, elapsedMs);
    }

    int Threshold(string operation)
    {
        if (options.ThresholdMs.TryGetValue(operation, out var exact))
            return exact;

        var separator = operation.IndexOf('.', StringComparison.Ordinal);
        if (separator >= 0 && options.ThresholdMs.TryGetValue(operation[..separator], out var group))
            return group;

        return options.ThresholdMs.TryGetValue("default", out var fallback) ? fallback : 1000;
    }
}
