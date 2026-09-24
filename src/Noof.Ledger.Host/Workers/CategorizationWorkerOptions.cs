namespace Noof.Ledger.Host.Workers;

internal sealed class CategorizationWorkerOptions
{
    // Several multiples of the model call's own 90-second timeout (Task 3), plus room for the DB
    // write and the Telegram edit, so a normal in-flight attempt never has its own lease reclaimed
    // out from under it by ReleaseExpiredLeasesAsync. Short enough that a genuinely crashed worker's
    // job is back in play within minutes rather than hours.
    public TimeSpan Lease { get; init; } = TimeSpan.FromMinutes(5);

    // Matches the schedule the Phase 1A job-queue task already documented (30s doubling reaches 64
    // minutes at attempt 8) and EfJobQueueTests' own default. Also feeds EfJobQueue's constructor
    // directly (see Program.cs) so the two never independently drift apart.
    public int MaxAttempts { get; init; } = 8;

    // Mirrors TelegramPollingService's idle poll interval: a captured message is typically
    // categorised within single-digit seconds without hammering ClaimAsync when the queue is empty.
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    // The base of the documented retry schedule (30s, 1m, 2m, 4m, ... doubling per attempt).
    public TimeSpan BackoffBase { get; init; } = TimeSpan.FromSeconds(30);

    // The ceiling the documented schedule reaches at the default MaxAttempts. Keeps growth bounded
    // if MaxAttempts is ever raised in configuration without this being revisited.
    public TimeSpan BackoffCap { get; init; } = TimeSpan.FromMinutes(64);

    // The exact limit the plan's flow diagram hardcodes for MerchantScan.Matches(rawText, aliases, 10).
    public int MerchantHintLimit { get; init; } = 10;

    // Each unknown merchant in one message costs its own model call (a knowing deviation from spec
    // section 11 - see "What this plan deliberately does NOT do"). Three is well past any real
    // message and bounds what a pathological one can spend. Lines past the cap keep their amount
    // and category and simply carry no merchant.
    public int MaxCanonicalizationsPerJob { get; init; } = 3;

    // How long the worker stops claiming ANY new job after an account-level failure (a bad key, no
    // credit, a revoked permission - see ModelCallExceptionExtensions.IsAccountLevel). Those three
    // statuses fail every queued job identically, not just the one that happened to run first;
    // without this, a mistyped key destroys a whole backlog's attempt budget within seconds instead
    // of giving the operator a window to fix it. Matches Lease's order of magnitude: long enough to
    // stop hammering, short enough that a fixed key recovers within minutes, not hours.
    public TimeSpan AccountCooldown { get; init; } = TimeSpan.FromMinutes(5);

    // The currency whose default wallet takes a record when neither the model nor the spending's own
    // currency picks one (M3). A line that states no currency takes its wallet's currency, not this. A
    // single hard default for now; see docs/BACKLOG.md for the deferred bot command that would set it.
    public string DefaultCurrency { get; init; } = "RSD";

    public TimeSpan ComputeBackoff(int attemptCount)
    {
        var exponent = Math.Max(0, attemptCount - 1);
        var seconds = Math.Min(BackoffCap.TotalSeconds, BackoffBase.TotalSeconds * Math.Pow(2, exponent));
        return TimeSpan.FromSeconds(seconds);
    }
}
