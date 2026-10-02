using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Jobs;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Application.Wallets;
using Noof.Ledger.Domain;
using Noof.Ledger.Host.Workers.RecordExchangeLogging;

namespace Noof.Ledger.Host.Workers;

// Claims only RecordExchange jobs and records a slip from its own evidence, without the model (spec §3,
// Recording 2) - so, like ExtractReceiptWorker's QR path, it never waits for a model key.
internal sealed class RecordExchangeWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    CategorizationWorkerOptions options,
    string workerId,
    IRecordEcho recordEcho,
    IDatabaseGate gate,
    IOperationTimer timer,
    ILogger<RecordExchangeWorker> logger)
    : BackgroundService
{
    const string SlipGone = "the transaction or its exchange slip no longer exists";

    static readonly JobKind[] ClaimableKinds = [JobKind.RecordExchange];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await gate.WaitUntilReadyAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var result = await RunTickAsync(stoppingToken);

            var delay = result == CategorizationTickResult.Processed ? TimeSpan.Zero : options.PollInterval;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, timeProvider, stoppingToken);
        }
    }

    public async Task<CategorizationTickResult> RunTickAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var jobQueue = scope.ServiceProvider.GetRequiredService<IJobQueue>();

            var now = timeProvider.GetUtcNow();
            using var release = timer.Start(logger, TimedOperations.DbReleaseExpiredLeases);
            var released = await jobQueue.ReleaseExpiredLeasesAsync(now, cancellationToken);
            release.Stop(onlyIfSlow: released == 0);

            using var claim = timer.Start(logger, TimedOperations.DbClaimJob);
            var job = await jobQueue.ClaimAsync(workerId, ClaimableKinds, options.Lease, cancellationToken);
            claim.Stop(onlyIfSlow: job is null);
            if (job is null)
                return CategorizationTickResult.Idle;

            await ProcessClaimedJobAsync(scope, jobQueue, job, cancellationToken);
            return CategorizationTickResult.Processed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.TickFailed(ex);
            return CategorizationTickResult.Failed;
        }
    }

    async Task ProcessClaimedJobAsync(IServiceScope scope, IJobQueue jobQueue, CategorizationJob job, CancellationToken cancellationToken)
    {
        var store = scope.ServiceProvider.GetRequiredService<ICategorizationStore>();
        var receiptStore = scope.ServiceProvider.GetRequiredService<IReceiptStore>();
        var walletDirectory = scope.ServiceProvider.GetRequiredService<IWalletDirectory>();
        var merchantDirectory = scope.ServiceProvider.GetRequiredService<IMerchantDirectory>();
        var notifier = scope.ServiceProvider.GetRequiredService<IChatNotifier>();

        var currentStage = TransactionStages.Categorized;

        using var logScope = TransactionLogScope.Begin(logger, job.TransactionId);
        timer.Record(logger, TimedOperations.JobQueueWait, (job.ClaimedAt ?? timeProvider.GetUtcNow()) - job.CreatedAt);

        try
        {
            var subject = await store.GetSubjectAsync(job.TransactionId, cancellationToken);
            if (subject is null || await receiptStore.GetExchangeSlipAsync(job.TransactionId, cancellationToken) is not { } slip)
            {
                logger.LogStageFailed(TransactionStages.StageFailed, currentStage, new InvalidOperationException(SlipGone));
                await jobQueue.FailAsync(job.Id, workerId, SlipGone, cancellationToken);
                return;
            }

            // Spec A-22: a reply queued while the photo was being read is claimed first and may already have recorded
            // the exchange with its own figures; the slip never overwrites a record that moved on.
            if (AlreadyApplied(subject))
            {
                logger.SlipAlreadyRecorded(job.TransactionId, subject.Status);
                await EchoAsync(store, notifier, job, cancellationToken);
                await SucceedQuietlyAsync(jobQueue, job, cancellationToken);
                return;
            }

            if (!ExchangeSlipMapper.TryRead(slip.Evidence, out var request, out var failure)
                || !request.TrySettle(out var settled, out failure))
            {
                await FailRecordAsync(store, notifier, jobQueue, job, failure, cancellationToken);
                return;
            }

            var wallets = await walletDirectory.ActiveAsync(cancellationToken);
            if (ExchangeSlipMapper.CashWalletOf(wallets, settled.From.Currency) is not { } fromWalletId
                || ExchangeSlipMapper.CashWalletOf(wallets, settled.To.Currency) is not { } toWalletId)
            {
                await FailRecordAsync(store, notifier, jobQueue, job, RecordFailureReason.LegCurrencyMismatch, cancellationToken);
                return;
            }

            if (fromWalletId == toWalletId)
            {
                await FailRecordAsync(store, notifier, jobQueue, job, RecordFailureReason.SameWallet, cancellationToken);
                return;
            }

            var facts = new TransferFacts(
                fromWalletId, settled.From, toWalletId, settled.To, settled.Fee, settled.FeeLeg, request.ConvertingRate,
                await ExchangeSlipMapper.VenueOfAsync(slip, merchantDirectory, cancellationToken));
            // Saving the slip already dated the record by the slip's local issue day (spec A-23), and nothing has
            // applied it since, so its date is the transfer's.
            var outcome = new CategorizationOutcome(
                [], subject.OccurredOn, JobKind.RecordExchange, Instruction: null, TransactionKind.Transfer, fromWalletId, Transfer: facts);

            logger.LogCategorized(TransactionStages.Categorized, TransactionKind.Transfer, fromWalletId,
                $"transfer {settled.From} to {settled.To}");
            currentStage = TransactionStages.Persisted;
            using (timer.Start(logger, TimedOperations.DbApplyCategorization))
                await store.ApplyAsync(job.TransactionId, outcome, cancellationToken);
            logger.LogPersisted(TransactionStages.Persisted, TransactionKind.Transfer);

            // From here on the transfer is committed, so nothing below may be treated as a job failure (the same
            // rule CategorizationWorker states): the echo and SucceedAsync each catch their own.
            await EchoAsync(store, notifier, job, cancellationToken);
            await SucceedQuietlyAsync(jobQueue, job, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogStageFailed(TransactionStages.StageFailed, currentStage, ex);
            await RetryAsync(store, notifier, jobQueue, job, ex.Message, cancellationToken);
        }
    }

    // "Applied" read off the record, which carries no revisions: Completed, or a transfer, line items or a balance
    // statement - which a record a correction applied keeps when the operator then cancels it.
    static bool AlreadyApplied(CategorizationSubject record) =>
        record.Status == TransactionStatus.Completed || record.Transfer is not null || record.Lines.Count > 0
        || record.Statement is not null;

    // A slip that cannot be recorded as it stands fails the record with its reason (spec §2), so the echo asks for
    // what is missing and a reply completes it as an ordinary correction. The store fails only a still-Captured
    // record, so a Cancel pressed after "Record anyway" stays a Cancel (spec A-22).
    async Task FailRecordAsync(
        ICategorizationStore store, IChatNotifier notifier, IJobQueue jobQueue, CategorizationJob job,
        RecordFailureReason reason, CancellationToken cancellationToken)
    {
        logger.SlipNotRecorded(job.TransactionId, reason);
        logger.LogStageFailed(TransactionStages.StageFailed, TransactionStages.Categorized, new InvalidOperationException(reason.ToString()));

        if (await jobQueue.FailAsync(job.Id, workerId, reason.ToString(), cancellationToken) != JobCompletionOutcome.Applied)
            return;

        await store.MarkFailedAsync(job.TransactionId, reason, cancellationToken);
        await EchoAsync(store, notifier, job, cancellationToken);
    }

    // No retry notice: nothing here waits on a provider, so a retry is a database blip the operator need not hear of.
    async Task RetryAsync(
        ICategorizationStore store, IChatNotifier notifier, IJobQueue jobQueue, CategorizationJob job, string error,
        CancellationToken cancellationToken)
    {
        var isLastAttempt = job.AttemptCount >= options.MaxAttempts;
        var runAfter = timeProvider.GetUtcNow() + options.ComputeBackoff(job.AttemptCount);

        if (await jobQueue.RetryAsync(job.Id, workerId, runAfter, error, cancellationToken) != JobCompletionOutcome.Applied
            || !isLastAttempt)
            return;

        await store.MarkFailedAsync(job.TransactionId, RecordFailureReason.None, cancellationToken);
        await EchoAsync(store, notifier, job, cancellationToken);
    }

    async Task EchoAsync(ICategorizationStore store, IChatNotifier notifier, CategorizationJob job, CancellationToken cancellationToken)
    {
        try
        {
            // Read back, not composed from the evidence: the echo shows what the database now holds (D4).
            if (await store.GetSubjectAsync(job.TransactionId, cancellationToken) is not { BotMessageId: { } messageId } record)
                return;

            await notifier.EditAsync(record.TelegramChatId, messageId, recordEcho.Compose(record), cancellationToken);
            logger.LogReplied(TransactionStages.Replied, messageId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogStageFailed(TransactionStages.StageFailed, TransactionStages.Replied, ex);
            logger.EchoFailed(ex, job.Id);
        }
    }

    async Task SucceedQuietlyAsync(IJobQueue jobQueue, CategorizationJob job, CancellationToken cancellationToken)
    {
        try
        {
            if (await jobQueue.SucceedAsync(job.Id, workerId, cancellationToken) == JobCompletionOutcome.NotOwned)
                logger.JobAlreadyReclaimed(job.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.SucceedAfterCommitFailed(ex, job.Id);
        }
    }
}
