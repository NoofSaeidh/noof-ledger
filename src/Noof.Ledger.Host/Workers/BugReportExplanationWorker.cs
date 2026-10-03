using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Diagnostics.BugReports;
using Noof.Ledger.Application.Diagnostics.Integrity;
using Noof.Ledger.Host.Workers.BugReportLogging;

namespace Noof.Ledger.Host.Workers;

internal enum BugReportTickResult { Idle = 0, Processed = 1, Failed = 2 }

// Its own queue rather than a categorization_jobs kind: that queue is keyed by a transaction, and a report may have none.
internal sealed class BugReportExplanationWorker(
    IServiceScopeFactory scopeFactory, TimeProvider timeProvider, CategorizationWorkerOptions options,
    IFindingText findingText, IDatabaseGate gate, IOperationTimer timer, ILogger<BugReportExplanationWorker> logger)
    : BackgroundService
{
    // Not the job queue's eight: an explanation is a convenience, and three outages in a row are enough to say so.
    public const int MaxAttempts = 3;

    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    // CategorizationWorker's rule (spec P-21): a refused account fails every report alike, so it spends no attempt and
    // explaining pauses instead; a key fixed later still gets the report explained.
    DateTimeOffset accountCooldownUntil = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await gate.WaitUntilReadyAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (await RunTickAsync(stoppingToken) != BugReportTickResult.Processed)
                await Task.Delay(PollInterval, timeProvider, stoppingToken);
        }
    }

    public async Task<BugReportTickResult> RunTickAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Read once, at the tick's start: NextDueAsync and the backoff both measure from it.
            var now = timeProvider.GetUtcNow();
            using var scope = scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IBugReportStore>();

            var explained = await ExplainNextAsync(scope.ServiceProvider, store, now, cancellationToken);

            return explained ? BugReportTickResult.Processed : BugReportTickResult.Idle;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.TickFailed(ex);
            return BugReportTickResult.Failed;
        }
    }

    async Task<bool> ExplainNextAsync(
        IServiceProvider services, IBugReportStore store, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Before taking a report, so a missing key never spends one of its attempts.
        if (!await services.GetRequiredService<IModelProvider>().IsConfiguredAsync(cancellationToken))
            return false;

        if (now < accountCooldownUntil)
            return false;

        if (await store.NextDueAsync(now, cancellationToken) is not { } report)
            return false;

        var snapshot = report.Snapshot ?? await TakeSnapshotAsync(store, report, cancellationToken);
        var request = findingText.ForReport(report.Text, snapshot.RecordSummary, snapshot.Findings, snapshot.TakenAt);
        var explainer = services.GetRequiredService<IFindingExplainer>();

        using var timing = timer.Start(logger, TimedOperations.JobExplainBugReport);
        Explanation explanation;
        try
        {
            explanation = await explainer.ExplainAsync(request, cancellationToken);
        }
        catch (ModelCallException ex) when (ex.IsAccountLevel())
        {
            accountCooldownUntil = now + options.AccountCooldown;
            logger.AccountRefused(report.Number, options.AccountCooldown);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RecordFailedAttemptAsync(store, report, now, ex, cancellationToken);
            return true;
        }

        await store.CompleteExplanationAsync(report.Id, explanation, cancellationToken);
        logger.Explained(report.Number, explanation.LooksLikeBug);
        return true;
    }

    async Task<BugReportSnapshot> TakeSnapshotAsync(IBugReportStore store, BugReportToExplain report, CancellationToken cancellationToken)
    {
        var snapshot = await store.TakeSnapshotAsync(report.Id, cancellationToken);
        if (snapshot.CollectionFailures is not null)
            logger.SnapshotIncomplete(report.Number);
        return snapshot;
    }

    async Task RecordFailedAttemptAsync(
        IBugReportStore store, BugReportToExplain report, DateTimeOffset now, Exception failure, CancellationToken cancellationToken)
    {
        var attempt = report.Attempts + 1;
        var state = await store.RecordFailedAttemptAsync(report.Id, now + options.ComputeBackoff(attempt), MaxAttempts, cancellationToken);

        if (state == BugExplanationState.Failed)
            logger.ExplanationGaveUp(report.Number, MaxAttempts);
        else
            logger.ExplanationAttemptFailed(report.Number, attempt, MaxAttempts, FailureType(failure));
    }

    static string FailureType(Exception failure) =>
        failure is ModelCallException model ? model.Kind.ToString() : failure.GetType().Name;
}
