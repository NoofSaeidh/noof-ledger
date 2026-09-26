using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Logging;

internal static partial class DatabaseLogLevelLog
{
    [LoggerMessage(EventId = 5401, Level = LogLevel.Information, Message = "Database log level changed from {PreviousLevel} to {Level}")]
    static partial void DatabaseLogLevelChangedCore(this ILogger logger, string previousLevel, string level);

    [LoggerMessage(EventId = 5402, Level = LogLevel.Information, Message = "Database log level is {Level}")]
    static partial void DatabaseLogLevelIsCore(this ILogger logger, string level);

    [LoggerMessage(EventId = 5403, Level = LogLevel.Warning, Message = "Could not read the stored database log level; retrying in {RetryDelay}")]
    public static partial void DatabaseLogLevelLoadFailed(this ILogger logger, Exception exception, TimeSpan retryDelay);

    // MEL's default formatter renders a null argument as "(null)" - the one line an operator would
    // grep for after logging stops needs to say "Off" instead, so every caller goes through these
    // wrappers rather than the LoggerMessage-generated methods directly.
    public static void DatabaseLogLevelChanged(this ILogger logger, LogSeverity? previousLevel, LogSeverity? level) =>
        logger.DatabaseLogLevelChangedCore(Text(previousLevel), Text(level));

    public static void DatabaseLogLevelIs(this ILogger logger, LogSeverity? level) =>
        logger.DatabaseLogLevelIsCore(Text(level));

    static string Text(LogSeverity? level) => level?.ToString() ?? "Off";
}
