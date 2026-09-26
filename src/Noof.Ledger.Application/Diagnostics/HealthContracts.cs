namespace Noof.Ledger.Application.Diagnostics;

public enum HealthLevel { Ok, Warning, Failing }

public sealed record HealthItem(string Name, HealthLevel Level, string Summary, DateTimeOffset CheckedAt, string LogCategory);

public sealed record SystemHealthReport(HealthLevel Overall, IReadOnlyList<HealthItem> Items);

public interface ISystemHealth
{
    Task<SystemHealthReport> GetAsync(bool fresh, CancellationToken cancellationToken);
}

public readonly record struct HealthOutcome(HealthLevel Level, string Summary)
{
    public static HealthOutcome Ok(string summary) => new(HealthLevel.Ok, summary);

    public static HealthOutcome Warning(string summary) => new(HealthLevel.Warning, summary);

    public static HealthOutcome Failing(string summary) => new(HealthLevel.Failing, summary);
}

// CheckAsync must return promptly once its token is cancelled, because SystemHealth's timeout can
// only cancel, never abandon.
public interface ISystemHealthCheck
{
    string Name { get; }

    int Order { get; }

    string LogCategory { get; }

    Task<HealthOutcome> CheckAsync(CancellationToken cancellationToken);
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

// The logger category (namespace/type prefix) whose rows the "Logs" link on /diagnostics should
// open for each check - not the check's own display name, which never appears in app_log.Source
// (that column holds Serilog's SourceContext, a fully-qualified logger category).
public static class HealthCheckLogCategories
{
    public static IReadOnlyDictionary<string, string> ByCheckName { get; } = new Dictionary<string, string>
    {
        [HealthCheckNames.Database] = "Noof.Ledger.Host.Startup.DatabaseStartupService",
        [HealthCheckNames.Migrations] = "Microsoft.EntityFrameworkCore.Migrations",
        [HealthCheckNames.Telegram] = "Noof.Ledger.Telegram",
        [HealthCheckNames.AiKeys] = "Noof.Ledger.Ai",
        [HealthCheckNames.Backup] = "Noof.Ledger.Host.Workers.BackupWorker",
        [HealthCheckNames.Disk] = "Noof.Ledger.Host.Diagnostics",
        [HealthCheckNames.LogSink] = "Noof.Ledger.Host.Logging",
    };
}
