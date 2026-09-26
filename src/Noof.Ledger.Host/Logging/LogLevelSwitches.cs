using Serilog.Core;
using Serilog.Events;

namespace Noof.Ledger.Host.Logging;

internal sealed class LogLevelSwitches
{
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
