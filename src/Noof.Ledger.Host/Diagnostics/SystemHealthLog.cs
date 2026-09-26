namespace Noof.Ledger.Host.Diagnostics;

internal static partial class SystemHealthLog
{
    [LoggerMessage(EventId = 5104, Level = LogLevel.Error, Message = "Health check {CheckName} threw")]
    public static partial void HealthCheckFailed(this ILogger logger, string checkName, Exception exception);

    [LoggerMessage(EventId = 5105, Level = LogLevel.Error, Message = "Health check {CheckName} gave no answer within {TimeoutSeconds} s")]
    public static partial void HealthCheckTimedOut(this ILogger logger, string checkName, int timeoutSeconds);
}
