namespace Noof.Ledger.Application.Diagnostics;

// A tri-state is what the store actually persists: no row (nothing chosen yet - Current stays at
// its compiled-in default), a row saying Off (nothing is written to app_log), or a row naming one
// of the six real levels. LogSeverity? alone cannot carry the "no row yet" case, so
// IDatabaseLogLevelStore.GetAsync returns DatabaseLogLevelSetting? - the outer null is "no row",
// this type's own Level null is "Off".
public readonly record struct DatabaseLogLevelSetting(LogSeverity? Level)
{
    public static readonly DatabaseLogLevelSetting Off = new(null);

    public bool IsOff => Level is null;

    public static DatabaseLogLevelSetting For(LogSeverity level) => new(level);
}
