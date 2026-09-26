using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Logging;

internal static partial class DatabaseLogLevelLog
{
    [LoggerMessage(EventId = 5401, Level = LogLevel.Information, Message = "Database log level changed from {PreviousLevel} to {Level}")]
    public static partial void DatabaseLogLevelChanged(this ILogger logger, LogSeverity previousLevel, LogSeverity level);

    [LoggerMessage(EventId = 5402, Level = LogLevel.Information, Message = "Database log level is {Level}")]
    public static partial void DatabaseLogLevelIs(this ILogger logger, LogSeverity level);

    [LoggerMessage(EventId = 5403, Level = LogLevel.Warning, Message = "Could not read the stored database log level; retrying in {RetryDelay}")]
    public static partial void DatabaseLogLevelLoadFailed(this ILogger logger, Exception exception, TimeSpan retryDelay);
}
