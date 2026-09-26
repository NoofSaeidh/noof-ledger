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
