namespace Noof.Ledger.Host.Logging;

// A sibling file, not nested inside Program (CLAUDE.md/contract: "[LoggerMessage] everywhere...a
// private static partial class Log inside each type or a sibling file"). Program.cs uses
// Serilog.Log unqualified throughout (Log.Logger, Log.Information, Log.Fatal, Log.CloseAndFlush);
// a type named Log nested inside Program would shadow that using-imported name for every
// unqualified reference in the same file, since member lookup prefers a nested type over an
// imported one with the same simple name.
internal static partial class ProgramLog
{
    [LoggerMessage(EventId = 1401, Level = LogLevel.Critical, Message = "{Message}")]
    public static partial void LoopbackGuardFailed(this ILogger logger, string message);
}
