namespace Noof.Ledger.Application.Diagnostics;

// Decision (c), 2026-09-26: per-level database retention lives only on the Log settings screen,
// persisted in app_setting - never in appsettings.json. These are the C# fallback defaults read
// when no operator has saved anything yet.
public sealed record LogRetentionDays(int Verbose, int Debug, int Information, int Warning, int Error, int Fatal)
{
    public static readonly LogRetentionDays Default = new(Verbose: 1, Debug: 1, Information: 30, Warning: 90, Error: 90, Fatal: 90);

    public int For(LogSeverity level) => level switch
    {
        LogSeverity.Verbose => Verbose,
        LogSeverity.Debug => Debug,
        LogSeverity.Information => Information,
        LogSeverity.Warning => Warning,
        LogSeverity.Error => Error,
        LogSeverity.Fatal => Fatal,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };
}
