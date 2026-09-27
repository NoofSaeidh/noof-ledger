using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Editing;
using Noof.Ledger.Application.Jobs;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Application.Wallets;
using Noof.Ledger.Domain;
using Noof.Ledger.Host.Workers.ReceiptCategorizationLogging;

namespace Noof.Ledger.Host.Workers;

// Claims only CategorizeReceipt jobs (R-2, R-3, R-6): a receipt's amounts always come from its own
// receipt_lines, never the model, so this worker never builds a CategorizationRequest or calls
// ICategorizer - it is a sibling of CategorizationWorker rather than a branch inside it because
// every one of its steps (the merchant lookup order, the wallet rule, the non-money receipt kinds)
// is receipt-specific, and folding them into CategorizationWorker's single ProcessClaimedJobAsync
// would make that method branch on "is this a receipt?" at nearly every line.
internal sealed class ReceiptCategorizationWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    CategorizationWorkerOptions options,
    string workerId,
    IRecordEcho recordEcho,
    TimeZoneInfo captureTimeZone,
    IDatabaseGate gate,
    IOperationTimer timer,
    ILogger<ReceiptCategorizationWorker> logger)
    : BackgroundService
{
    const string FallbackCategorySlug = "other";

    // For CategorizationWorker's own reason: an account-level model failure fails every queued job
    // identically, so claiming pauses here too rather than burning through the backlog.
    DateTimeOffset accountCooldownUntil = DateTimeOffset.MinValue;

    static readonly JobKind[] ClaimableKinds = [JobKind.CategorizeReceipt];

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
            var modelProvider = scope.ServiceProvider.GetRequiredService<IModelProvider>();

            var now = timeProvider.GetUtcNow();
            using var release = timer.Start(logger, TimedOperations.DbReleaseExpiredLeases);
            var released = await jobQueue.ReleaseExpiredLeasesAsync(now, cancellationToken);
            release.Stop(onlyIfSlow: released == 0);

            // Checked before claiming, for CategorizationWorker's own reason: a claim spends an
            // attempt and nothing gives it back.
            var ordinaryClaimGated = !await modelProvider.IsConfiguredAsync(cancellationToken) || now < accountCooldownUntil;

            if (ordinaryClaimGated)
            {
                // Tried only while the ordinary claim is gated (no key configured, or inside the
                // account cooldown): a Copy/Training/Proforma/Advance receipt is cancelled without
                // ever calling the model (ReportNotRecordedAsync), so it must not be stuck behind
                // IModelProvider like a money receipt. When the ordinary claim isn't gated it already
                // covers non-money receipts too (ProcessClaimedJobAsync's IsNonMoneyKind check), so
                // trying this claim on every tick would only add an extra UPDATE and let a non-money
                // receipt jump the ordinary queue's run_after order.
                using var claimNonMoney = timer.Start(logger, TimedOperations.DbClaimJob);
                var nonMoneyJob = await jobQueue.ClaimNonMoneyReceiptAsync(workerId, options.Lease, cancellationToken);
                claimNonMoney.Stop(onlyIfSlow: nonMoneyJob is null);
                if (nonMoneyJob is null)
                    return CategorizationTickResult.Idle;

                await ProcessClaimedJobAsync(scope, jobQueue, nonMoneyJob, cancellationToken);
                return CategorizationTickResult.Processed;
            }

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

    async Task ProcessClaimedJobAsync(
        IServiceScope scope, IJobQueue jobQueue, CategorizationJob job, CancellationToken cancellationToken)
    {
        var store = scope.ServiceProvider.GetRequiredService<ICategorizationStore>();
        var receiptStore = scope.ServiceProvider.GetRequiredService<IReceiptStore>();
        var categoryCatalog = scope.ServiceProvider.GetRequiredService<ICategoryCatalog>();
        var merchantDirectory = scope.ServiceProvider.GetRequiredService<IMerchantDirectory>();
        var walletDirectory = scope.ServiceProvider.GetRequiredService<IWalletDirectory>();
        var receiptCategorizer = scope.ServiceProvider.GetRequiredService<IReceiptCategorizer>();
        var notifier = scope.ServiceProvider.GetRequiredService<IChatNotifier>();
        var recordEditor = scope.ServiceProvider.GetRequiredService<IRecordEditor>();

        CategorizationSubject? subject = null;
        var currentStage = TransactionStages.Categorized;

        using var logScope = TransactionLogScope.Begin(logger, job.TransactionId);
        timer.Record(logger, TimedOperations.JobQueueWait, (job.ClaimedAt ?? timeProvider.GetUtcNow()) - job.CreatedAt);
        using var jobTiming = timer.Start(logger, TimedOperations.JobCategorizeReceipt);

        try
        {
            subject = await store.GetSubjectAsync(job.TransactionId, cancellationToken);
            if (subject is not { } sub)
            {
                await FailTerminallyAsync(
                    jobQueue, store, notifier, job, null, "the transaction this job points at no longer exists", currentStage, cancellationToken);
                return;
            }

            var receipt = await receiptStore.GetByTransactionAsync(job.TransactionId, cancellationToken);
            if (receipt is null)
            {
                await FailTerminallyAsync(
                    jobQueue, store, notifier, job, subject, "no receipt is recorded for this transaction", currentStage, cancellationToken);
                return;
            }

            if (receipt.Kind.IsNonMoneyKind())
            {
                await ReportNotRecordedAsync(recordEditor, notifier, job, sub, receipt.Kind, cancellationToken);
                await SucceedQuietlyAsync(jobQueue, job, cancellationToken);
                return;
            }

            var aliases = await merchantDirectory.AliasesAsync(cancellationToken);
            var aliasByFolded = aliases.ToDictionary(alias => alias.Folded, alias => alias);

            var merchantId = await KnownMerchantIdAsync(merchantDirectory, aliasByFolded, receipt, cancellationToken);
            var merchantKnown = merchantId is not null;

            var categories = await categoryCatalog.ActiveAsync(cancellationToken);
            var wallets = await walletDirectory.ActiveAsync(cancellationToken);

            var request = new ReceiptCategorizationRequest(
                [.. receipt.Lines.Select(line => new ReceiptLineToCategorize(line.Ordinal, line.Name, line.Quantity, line.Total))],
                receipt.SellerName, receipt.SellerTaxId, merchantKnown, sub.RawText, job.Instruction);

            var categorization = await receiptCategorizer.CategorizeAsync(request, cancellationToken);

            merchantId ??= await ResolveNewMerchantAsync(merchantDirectory, receipt, categorization, cancellationToken);

            if (merchantId is { } knownMerchantId && receipt.SellerTaxId is { Length: > 0 } taxId)
                await merchantDirectory.LinkTaxIdAsync(knownMerchantId, taxId, cancellationToken);

            var walletId = await ResolveWalletAsync(walletDirectory, job, sub, categorization, receipt, wallets, cancellationToken);
            if (walletId is null)
            {
                await FailTerminallyAsync(jobQueue, store, notifier, job, subject, "no wallet to record into", currentStage, cancellationToken);
                return;
            }

            var slugByOrdinal = categorization.Lines.ToDictionary(line => line.Ordinal, line => line.CategorySlug);
            var items = new List<CategorizedLineItem>(receipt.Lines.Count);
            foreach (var line in receipt.Lines.OrderBy(line => line.Ordinal))
            {
                if (!slugByOrdinal.TryGetValue(line.Ordinal, out var slug))
                {
                    logger.MissingOrdinal(line.Ordinal, FallbackCategorySlug);
                    slug = FallbackCategorySlug;
                }

                var categoryId = categories.First(category => category.Slug == slug).Id;
                items.Add(new CategorizedLineItem(
                    line.Name, new Money(line.Total, receipt.Currency), categoryId, merchantId, line.Ordinal, line.Id));
            }

            var transactionKind = receipt.Kind == ReceiptKind.Refund ? TransactionKind.Income : TransactionKind.Expense;
            var occurredOn = receipt.IssuedAt is { } issuedAt
                ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(issuedAt, captureTimeZone).DateTime)
                : sub.OccurredOn;

            logger.LogCategorized(TransactionStages.Categorized, transactionKind, walletId,
                $"{items.Count} receipt line(s), total {receipt.Total} {receipt.Currency}");

            var outcome = new CategorizationOutcome(items, occurredOn, JobKind.CategorizeReceipt, job.Instruction, transactionKind, walletId);
            currentStage = TransactionStages.Persisted;
            await store.ApplyAsync(job.TransactionId, outcome, cancellationToken);
            logger.LogPersisted(TransactionStages.Persisted, transactionKind);

            // From here on, as in CategorizationWorker: the line items and status are already
            // committed, so nothing past this line may be treated as a job failure.
            await EchoAsync(store, receiptStore, notifier, job, categorization.UnsupportedChange, cancellationToken);

            try
            {
                var succeedOutcome = await jobQueue.SucceedAsync(job.Id, workerId, cancellationToken);
                if (succeedOutcome == JobCompletionOutcome.NotOwned)
                    logger.JobAlreadyReclaimed(job.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.SucceedAfterCommitFailed(ex, job.Id);
            }
        }
        catch (ModelCallException ex) when (ex.IsAccountLevel())
        {
            accountCooldownUntil = timeProvider.GetUtcNow() + options.AccountCooldown;
            logger.AccountLevelFailure(job.Id, ex.Message, options.AccountCooldown);
            await HandleModelFailureAsync(jobQueue, store, notifier, job, subject, ModelFailureKind.Transient, ex.Message, currentStage, cancellationToken, ex);
        }
        catch (ModelCallException ex)
        {
            await HandleModelFailureAsync(jobQueue, store, notifier, job, subject, ex.Kind, ex.Message, currentStage, cancellationToken, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await HandleModelFailureAsync(jobQueue, store, notifier, job, subject, ModelFailureKind.Transient, ex.Message, currentStage, cancellationToken, ex);
        }
    }

    static async Task<Guid?> KnownMerchantIdAsync(
        IMerchantDirectory merchantDirectory, Dictionary<string, MerchantAliasEntry> aliasByFolded, ReceiptView receipt,
        CancellationToken cancellationToken)
    {
        if (receipt.SellerTaxId is { Length: > 0 } taxId
            && await merchantDirectory.FindByTaxIdAsync(taxId, cancellationToken) is { } byTaxId)
            return byTaxId;

        if (receipt.SellerName is { Length: > 0 } sellerName
            && aliasByFolded.TryGetValue(MerchantName.Fold(sellerName), out var alias))
            return alias.MerchantId;

        return null;
    }

    static async Task<Guid?> ResolveNewMerchantAsync(
        IMerchantDirectory merchantDirectory, ReceiptView receipt, ReceiptCategorization categorization, CancellationToken cancellationToken)
    {
        var canonicalName = categorization.MerchantCanonicalName ?? receipt.SellerName;
        if (canonicalName is not { Length: > 0 })
            return null;

        var folded = MerchantName.Fold(receipt.SellerName is { Length: > 0 } sellerName ? sellerName : canonicalName);
        return await merchantDirectory.LinkAliasAsync(folded, canonicalName, cancellationToken);
    }

    static async Task<Guid?> ResolveWalletAsync(
        IWalletDirectory walletDirectory, CategorizationJob job, CategorizationSubject subject, ReceiptCategorization categorization,
        ReceiptView receipt, IReadOnlyList<WalletOption> wallets, CancellationToken cancellationToken)
    {
        if (categorization.WalletId is { } named && wallets.Any(wallet => wallet.Id == named))
            return named;

        // N-3 (Phase 6 re-review): the same rule CategorizationWorker.KeepingTheRecordsWallet applies
        // to a plain-text Correct job - job.Instruction present means this is a correction of an
        // already-recorded receipt, not the first categorization, so a correction naming no wallet
        // means "leave it where it is", never "the default". The payment/currency default below is a
        // first-categorization fallback only; re-resolving it on every correction would silently move
        // the record back whenever a later correction (or a changed wallet default) answers
        // wallet_id as null.
        if (job.Instruction is not null && subject.WalletId is { } current && wallets.Any(wallet => wallet.Id == current))
            return current;

        if (receipt.PaymentMethod is PaymentMethod.Card or PaymentMethod.Cash
            && await walletDirectory.DefaultForPaymentAsync(receipt.PaymentMethod.Value, cancellationToken) is { } forPayment)
            return forPayment;

        return DefaultWalletFor(receipt.Currency.Value, wallets);
    }

    static Guid? DefaultWalletFor(string currency, IReadOnlyList<WalletOption> wallets) =>
        wallets.FirstOrDefault(wallet =>
            wallet.IsDefaultForCurrency && string.Equals(wallet.Currency.Value, currency, StringComparison.OrdinalIgnoreCase))?.Id;

    // M-4 (Phase 6 final review): a copy/training/proforma/advance slip is a deliberate non-post, the
    // same kind of decision a duplicate receipt already gets (Cancelled, never Failed) - not a
    // processing error, so it is Cancelled through IRecordEditor rather than MarkFailedAsync. That
    // keeps Failed meaning "something broke", and CancelAsync's own revision plus this StageFailed
    // row (at Categorized, since nothing ever reached Persisted) give the trace page something to
    // show instead of Extracted-then-silence.
    async Task ReportNotRecordedAsync(
        IRecordEditor recordEditor, IChatNotifier notifier, CategorizationJob job, CategorizationSubject subject,
        ReceiptKind kind, CancellationToken cancellationToken)
    {
        logger.ReceiptNotRecorded(kind.ToString(), job.TransactionId);
        logger.LogStageFailed(TransactionStages.StageFailed, TransactionStages.Categorized,
            new InvalidOperationException($"Receipt kind {kind} is not a purchase; nothing was recorded"));
        await recordEditor.CancelAsync(job.TransactionId, cancellationToken);

        if (subject.BotMessageId is not { } messageId)
            return;

        try
        {
            await notifier.EditAsync(subject.TelegramChatId, messageId, recordEcho.ComposeReceiptNotRecorded(kind), cancellationToken);
            logger.LogReplied(TransactionStages.Replied, messageId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogStageFailed(TransactionStages.StageFailed, TransactionStages.Replied, ex);
            logger.EchoFailed(ex, job.Id);
        }
    }

    async Task EchoAsync(
        ICategorizationStore store, IReceiptStore receiptStore, IChatNotifier notifier, CategorizationJob job,
        UnsupportedChangeKind unsupportedChange, CancellationToken cancellationToken)
    {
        try
        {
            // Read back, not composed from the proposal (D4): the echo shows what the database now holds.
            if (await store.GetSubjectAsync(job.TransactionId, cancellationToken) is not { BotMessageId: { } messageId } record)
                return;

            if (await receiptStore.GetByTransactionAsync(job.TransactionId, cancellationToken) is not { } receipt)
                return;

            await notifier.EditAsync(
                record.TelegramChatId, messageId, recordEcho.ComposeReceipt(record, receipt, unsupportedChange), cancellationToken);
            logger.LogReplied(TransactionStages.Replied, messageId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogStageFailed(TransactionStages.StageFailed, TransactionStages.Replied, ex);
            logger.EchoFailed(ex, job.Id);
        }
    }

    async Task HandleModelFailureAsync(
        IJobQueue jobQueue, ICategorizationStore store, IChatNotifier notifier,
        CategorizationJob job, CategorizationSubject? subject, ModelFailureKind kind, string error, string failedStage,
        CancellationToken cancellationToken, Exception? exception = null)
    {
        if (kind == ModelFailureKind.Terminal)
        {
            await FailTerminallyAsync(jobQueue, store, notifier, job, subject, error, failedStage, cancellationToken, exception);
            return;
        }

        var actualException = exception ?? new InvalidOperationException(error);
        logger.LogStageFailed(TransactionStages.StageFailed, failedStage, actualException);

        var isLastAttempt = job.AttemptCount >= options.MaxAttempts;
        var runAfter = timeProvider.GetUtcNow() + options.ComputeBackoff(job.AttemptCount);
        var outcome = await jobQueue.RetryAsync(job.Id, workerId, runAfter, error, cancellationToken);

        if (outcome != JobCompletionOutcome.Applied)
            return;

        if (isLastAttempt)
        {
            await NotifyFailureAsync(store, notifier, subject, cancellationToken);
            return;
        }

        await ReportRetryAsync(notifier, subject, actualException, runAfter, cancellationToken);
    }

    async Task ReportRetryAsync(
        IChatNotifier notifier, CategorizationSubject? subject, Exception exception, DateTimeOffset runAfter,
        CancellationToken cancellationToken)
    {
        if (subject is not { BotMessageId: { } messageId } sub)
            return;

        var localRunAfter = TimeZoneInfo.ConvertTime(runAfter, captureTimeZone);
        var notice = recordEcho.ComposeReceiptCategorizationRetryNotice(exception, localRunAfter);

        try
        {
            await notifier.EditAsync(sub.TelegramChatId, messageId, notice, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.RetryNoticeEditFailed(ex, messageId, sub.TransactionId);
        }
    }

    async Task FailTerminallyAsync(
        IJobQueue jobQueue, ICategorizationStore store, IChatNotifier notifier,
        CategorizationJob job, CategorizationSubject? subject, string error, string failedStage, CancellationToken cancellationToken,
        Exception? exception = null)
    {
        logger.LogStageFailed(TransactionStages.StageFailed, failedStage, exception ?? new InvalidOperationException(error));

        var outcome = await jobQueue.FailAsync(job.Id, workerId, error, cancellationToken);
        if (outcome == JobCompletionOutcome.Applied)
            await NotifyFailureAsync(store, notifier, subject, cancellationToken);
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

    async Task NotifyFailureAsync(
        ICategorizationStore store, IChatNotifier notifier, CategorizationSubject? subject, CancellationToken cancellationToken)
    {
        if (subject is not { BotMessageId: { } messageId } sub)
            return;

        await store.MarkFailedAsync(sub.TransactionId, cancellationToken);

        try
        {
            await notifier.EditAsync(sub.TelegramChatId, messageId, recordEcho.Failure, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.FailureEditFailed(ex, messageId, sub.TransactionId);
        }
    }
}
