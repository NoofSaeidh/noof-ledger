using Serilog.Core;
using Serilog.Events;

namespace Noof.Ledger.Host.Logging;

internal sealed class LogLevelSwitches
{
    // Decision (a), 2026-09-26: the database level can be turned Off entirely. Serilog's
    // LogEventLevel tops out at Fatal (5); no real LogEvent is ever >= this sentinel, so a sink
    // filtered with levelSwitch: Database set to it receives nothing - and RecomputeRoot's Min()
    // naturally leaves it out of the root computation too, since it is never the smallest of the
    // three.
    public const LogEventLevel Off = (LogEventLevel)((int)LogEventLevel.Fatal + 1);

    readonly Lock gate = new();
    LogEventLevel file = LogEventLevel.Information;
    LogEventLevel console = LogEventLevel.Information;

    public LoggingLevelSwitch Root { get; } = new(LogEventLevel.Information);
    public LoggingLevelSwitch Database { get; } = new(LogEventLevel.Information);

    public void SetFileLevel(LogEventLevel level)
    {
        lock (gate)
        {
            file = level;
            RecomputeRoot();
        }
    }

    public void SetConsoleLevel(LogEventLevel level)
    {
        lock (gate)
        {
            console = level;
            RecomputeRoot();
        }
    }

    public void SetDatabaseLevel(LogEventLevel level)
    {
        lock (gate)
        {
            Database.MinimumLevel = level;
            RecomputeRoot();
        }
    }

    void RecomputeRoot() => Root.MinimumLevel = Min(Min(file, console), Database.MinimumLevel);

    static LogEventLevel Min(LogEventLevel a, LogEventLevel b) => a < b ? a : b;
}
