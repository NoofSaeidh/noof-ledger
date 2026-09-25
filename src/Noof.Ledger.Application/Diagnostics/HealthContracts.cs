namespace Noof.Ledger.Application.Diagnostics;

public enum HealthLevel { Ok, Warning, Failing }

public sealed record HealthItem(string Name, HealthLevel Level, string Summary, DateTimeOffset CheckedAt);

public sealed record SystemHealthReport(HealthLevel Overall, IReadOnlyList<HealthItem> Items);

public interface ISystemHealth
{
    Task<SystemHealthReport> GetAsync(bool fresh, CancellationToken cancellationToken);
}

public enum PollFailure { Network, Unauthorized, Other }

public interface IPollingHeartbeat
{
    DateTimeOffset? LastSuccessAt { get; }

    (DateTimeOffset At, PollFailure Failure)? LastFailure { get; }

    void RecordSuccess(DateTimeOffset at);

    void RecordFailure(DateTimeOffset at, PollFailure failure);
}

public static class HealthCheckNames
{
    public const string Database = "Database";
    public const string Migrations = "Migrations";
    public const string Telegram = "Telegram";
    public const string AiKeys = "AI keys";
    public const string Backup = "Backup";
    public const string Disk = "Disk";
    public const string LogSink = "Log sink";

    public static IReadOnlyList<string> Ordered { get; } =
        [Database, Migrations, Telegram, AiKeys, Backup, Disk, LogSink];
}
