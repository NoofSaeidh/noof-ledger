using Noof.Ledger.Application.Diagnostics;
using Serilog.Events;

namespace Noof.Ledger.Host.Logging;

internal sealed class DatabaseLogLevel(LogLevelSwitches switches, IServiceScopeFactory scopeFactory, ILogger<DatabaseLogLevel> logger)
    : IDatabaseLogLevel
{
    public static readonly IReadOnlyList<LogSeverity> Choices = [LogSeverity.Verbose, LogSeverity.Debug, LogSeverity.Information];

    IReadOnlyList<LogSeverity> IDatabaseLogLevel.Choices => Choices;

    public LogSeverity Current => (LogSeverity)switches.Database.MinimumLevel;

    public async Task SetAsync(LogSeverity level, CancellationToken cancellationToken)
    {
        if (!Choices.Contains(level))
            throw new ArgumentOutOfRangeException(nameof(level), level, $"{level} is not a valid database log level.");

        var previous = Current;

        using (var scope = scopeFactory.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IDatabaseLogLevelStore>();
            await store.SaveAsync(level, cancellationToken);
        }

        switches.SetDatabaseLevel((LogEventLevel)level);
        logger.DatabaseLogLevelChanged(previous, level);
    }

    internal async Task LoadAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDatabaseLogLevelStore>();
        var stored = await store.GetAsync(cancellationToken);

        if (stored is { } level && Choices.Contains(level))
        {
            switches.SetDatabaseLevel((LogEventLevel)level);
            logger.DatabaseLogLevelIs(level);
        }
    }
}
