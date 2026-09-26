using Noof.Ledger.Application.Diagnostics;
using Serilog.Events;

namespace Noof.Ledger.Host.Logging;

internal sealed class DatabaseLogLevel(LogLevelSwitches switches, IServiceScopeFactory scopeFactory, ILogger<DatabaseLogLevel> logger)
    : IDatabaseLogLevel
{
    // Decision (a), 2026-09-26: every real level is offered, not just Verbose/Debug/Information -
    // the operator withdrew the earlier "capped at Information" rule. Off is not a LogSeverity (it
    // is not a level anything gets stored under), so it is not a member of Choices; it is
    // represented by Current/SetAsync taking LogSeverity? instead.
    public static readonly IReadOnlyList<LogSeverity> Choices = Enum.GetValues<LogSeverity>();

    IReadOnlyList<LogSeverity> IDatabaseLogLevel.Choices => Choices;

    public LogSeverity? Current => switches.Database.MinimumLevel == LogLevelSwitches.Off
        ? null
        : (LogSeverity)switches.Database.MinimumLevel;

    public async Task SetAsync(LogSeverity? level, CancellationToken cancellationToken)
    {
        if (level is { } value && !Choices.Contains(value))
            throw new ArgumentOutOfRangeException(nameof(level), value, $"{value} is not a valid database log level.");

        var previous = Current;
        var setting = level is { } chosen ? DatabaseLogLevelSetting.For(chosen) : DatabaseLogLevelSetting.Off;

        using (var scope = scopeFactory.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IDatabaseLogLevelStore>();
            await store.SaveAsync(setting, cancellationToken);
        }

        switches.SetDatabaseLevel(ToSerilogLevel(level));
        logger.DatabaseLogLevelChanged(previous, level);
    }

    internal async Task LoadAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDatabaseLogLevelStore>();
        var stored = await store.GetAsync(cancellationToken);

        if (stored is not { } setting)
            return;

        if (setting.IsOff)
        {
            switches.SetDatabaseLevel(LogLevelSwitches.Off);
            logger.DatabaseLogLevelIs(null);
        }
        else if (Choices.Contains(setting.Level!.Value))
        {
            switches.SetDatabaseLevel((LogEventLevel)setting.Level.Value);
            logger.DatabaseLogLevelIs(setting.Level.Value);
        }
    }

    static LogEventLevel ToSerilogLevel(LogSeverity? level) => level is { } value ? (LogEventLevel)value : LogLevelSwitches.Off;
}
