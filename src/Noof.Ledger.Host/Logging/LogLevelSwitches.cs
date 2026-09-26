using Serilog.Core;
using Serilog.Events;

namespace Noof.Ledger.Host.Logging;

internal sealed class LogLevelSwitches
{
    readonly Lock gate = new();
    LogEventLevel file = LogEventLevel.Information;

    public LoggingLevelSwitch Root { get; } = new(LogEventLevel.Information);
    public LoggingLevelSwitch Database { get; } = new(LogEventLevel.Information);

    public void SetFileLevel(LogEventLevel level)
    {
        lock (gate)
        {
            file = level;
            Root.MinimumLevel = Min(file, Database.MinimumLevel);
        }
    }

    public void SetDatabaseLevel(LogEventLevel level)
    {
        lock (gate)
        {
            Database.MinimumLevel = level;
            Root.MinimumLevel = Min(file, level);
        }
    }

    static LogEventLevel Min(LogEventLevel a, LogEventLevel b) => a < b ? a : b;
}
