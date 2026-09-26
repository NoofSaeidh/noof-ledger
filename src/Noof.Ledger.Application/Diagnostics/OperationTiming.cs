using Microsoft.Extensions.Logging;

namespace Noof.Ledger.Application.Diagnostics;

// A class, not a struct: Stop is idempotent, so `using` plus an explicit Stop never logs twice.
public sealed class OperationTiming : IDisposable
{
    readonly OperationTimer timer;
    readonly TimeProvider clock;
    readonly ILogger logger;
    readonly string operation;
    readonly TimeSpan expectedWait;
    readonly long startTimestamp;

    TimeSpan? elapsed;

    internal OperationTiming(IOperationTimer timer, TimeProvider clock, ILogger logger, string operation, TimeSpan expectedWait)
    {
        this.timer = (OperationTimer)timer;
        this.clock = clock;
        this.logger = logger;
        this.operation = operation;
        this.expectedWait = expectedWait;
        startTimestamp = clock.GetTimestamp();
    }

    public TimeSpan Stop(bool onlyIfSlow = false)
    {
        if (elapsed is { } already)
            return already;

        var value = clock.GetElapsedTime(startTimestamp);
        elapsed = value;
        timer.LogOutcome(logger, operation, value, onlyIfSlow, expectedWait);
        return value;
    }

    public void Dispose() => Stop();
}
