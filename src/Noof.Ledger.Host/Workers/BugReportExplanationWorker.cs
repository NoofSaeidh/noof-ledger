using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
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

    // Per process, on purpose: a chat that blocks the bot would otherwise write a Warning every tick; after a restart
    // it warns once more.
    readonly HashSet<Guid> undeliverable = [];

    // A paid answer whose write failed: the next tick that takes its report writes it instead of asking the model again,
    // and a failed write spends no attempt (P-11). Per process; after a restart the model is asked once more.
    readonly Dictionary<Guid, Explanation> unwritten = [];

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

            var explained = await TryExplainNextAsync(scope.ServiceProvider, store, now, cancellationToken);
            var delivered = await DeliverAsync(scope.ServiceProvider.GetRequiredService<IChatNotifier>(), store, cancellationToken);

            return explained switch
            {
                null => BugReportTickResult.Failed,
                true => BugReportTickResult.Processed,
                false => delivered ? BugReportTickResult.Processed : BugReportTickResult.Idle,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.TickFailed(ex);
            return BugReportTickResult.Failed;
        }
    }

    // Its own failure boundary, so a report that fails on every tick never holds back another report's reply (P-13).
    async Task<bool?> TryExplainNextAsync(
        IServiceProvider services, IBugReportStore store, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            return await ExplainNextAsync(services, store, now, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.TickFailed(ex);
            return null;
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
        {
            // A failed write leaves its report due, so nothing due means each unwritten answer's report was closed or
            // explained elsewhere.
            unwritten.Clear();
            return false;
        }

        if (unwritten.TryGetValue(report.Id, out var answered))
        {
            await StoreExplanationAsync(store, report, answered, cancellationToken);
            return true;
        }

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

        unwritten[report.Id] = explanation;
        await StoreExplanationAsync(store, report, explanation, cancellationToken);
        return true;
    }

    async Task StoreExplanationAsync(
        IBugReportStore store, BugReportToExplain report, Explanation explanation, CancellationToken cancellationToken)
    {
        await store.CompleteExplanationAsync(report.Id, explanation, cancellationToken);
        unwritten.Remove(report.Id);
        logger.Explained(report.Number, explanation.LooksLikeBug);
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

    // A failed send spends no attempt and never calls the model again: the answer is stored, only the reply is owed.
    // Storing a sent reply's id stays outside the send's catch, so a database fault there is the tick's 1901, not 1905.
    async Task<bool> DeliverAsync(IChatNotifier chatNotifier, IBugReportStore store, CancellationToken cancellationToken)
    {
        var delivered = false;
        foreach (var delivery in await store.PendingDeliveriesAsync(cancellationToken))
        {
            if (await TrySendReplyAsync(chatNotifier, delivery, cancellationToken) is not { } replyId)
                continue;

            await store.MarkDeliveredAsync(delivery.Id, replyId, cancellationToken);
            undeliverable.Remove(delivery.Id);
            logger.ReplyDelivered(delivery.Number);
            delivered = true;
        }

        return delivered;
    }

    async Task<int?> TrySendReplyAsync(IChatNotifier chatNotifier, BugReportDelivery delivery, CancellationToken cancellationToken)
    {
        try
        {
            return await chatNotifier.ReplyToBugReportAsync(
                delivery.ChatId, delivery.MessageId, BugReportReplies.Compose(delivery), CloseButtonNumber(delivery), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (undeliverable.Add(delivery.Id))
                logger.ReplyFailed(delivery.Number, FailureType(ex));
            else
                logger.ReplyStillFailing(delivery.Number, FailureType(ex));
            return null;
        }
    }

    // Only an answer that blames the data, not the app, offers to close the report from the chat.
    static int? CloseButtonNumber(BugReportDelivery delivery) =>
        delivery is { State: BugExplanationState.Done, LooksLikeBug: false } ? delivery.Number : null;

    static string FailureType(Exception failure) =>
        failure is ModelCallException model ? model.Kind.ToString() : failure.GetType().Name;
}
