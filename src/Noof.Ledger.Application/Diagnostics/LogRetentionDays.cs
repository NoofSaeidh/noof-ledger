namespace Noof.Ledger.Application.Diagnostics;

// Decision (c), 2026-09-26: per-level database retention lives only on the Log settings screen,
// persisted in app_setting - never in appsettings.json. These are the C# fallback defaults read
// when no operator has saved anything yet.
public sealed record LogRetentionDays(int Verbose, int Debug, int Information, int Warning, int Error, int Fatal)
{
    public const int MinDays = 1;
    public const int MaxDays = 3650;

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

    public static bool IsValid(int days) => days is >= MinDays and <= MaxDays;

    // A stored row can carry a missing, zero or out-of-range value - a legacy row from before a
    // level existed deserialises that member as 0, and nothing stops a hand-edited app_setting row
    // from carrying a negative one either. Each member falls back to Default's own value
    // independently, rather than discarding the whole row, so one bad field never resets every
    // other level an operator already tuned.
    public LogRetentionDays SanitizedOrDefault() => new(
        IsValid(Verbose) ? Verbose : Default.Verbose,
        IsValid(Debug) ? Debug : Default.Debug,
        IsValid(Information) ? Information : Default.Information,
        IsValid(Warning) ? Warning : Default.Warning,
        IsValid(Error) ? Error : Default.Error,
        IsValid(Fatal) ? Fatal : Default.Fatal);
}
