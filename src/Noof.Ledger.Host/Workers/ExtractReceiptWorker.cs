using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Editing;
using Noof.Ledger.Application.Jobs;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Domain;
using Noof.Ledger.Host.Workers.ExtractReceiptLogging;

namespace Noof.Ledger.Host.Workers;

// Claims only ExtractReceipt jobs. Photo or link -> QR (offline) -> the Tax Administration -> vision,
// every degraded step visible on the trace (docs/specs/2026-09-25-receipts-design.md §2).
internal sealed class ExtractReceiptWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    CategorizationWorkerOptions options,
    string workerId,
    IRecordEcho recordEcho,
    TimeZoneInfo captureTimeZone,
    IDatabaseGate gate,
    IOperationTimer timer,
    ILogger<ExtractReceiptWorker> logger)
    : BackgroundService
{
    static readonly JobKind[] ClaimableKinds = [JobKind.ExtractReceipt];

    DateTimeOffset accountCooldownUntil = DateTimeOffset.MinValue;

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

            if (now < accountCooldownUntil)
                return CategorizationTickResult.Idle;

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
        var recordEditor = scope.ServiceProvider.GetRequiredService<IRecordEditor>();
        var notifier = scope.ServiceProvider.GetRequiredService<IChatNotifier>();

        CategorizationSubject? record = null;

        using var logScope = TransactionLogScope.Begin(logger, job.TransactionId);
        timer.Record(logger, TimedOperations.JobQueueWait, (job.ClaimedAt ?? timeProvider.GetUtcNow()) - job.CreatedAt);
        using var jobTiming = timer.Start(logger, TimedOperations.JobExtractReceipt);

        try
        {
            record = await store.GetSubjectAsync(job.TransactionId, cancellationToken);
            if (record is null)
            {
                logger.LogStageFailed(TransactionStages.StageFailed, TransactionStages.Extracted,
                    new InvalidOperationException("the transaction this job points at no longer exists"));
                await jobQueue.FailAsync(job.Id, workerId, "the transaction this job points at no longer exists", cancellationToken);
                return;
            }

            // C-1: a lease-expiry replay of a job whose earlier run already committed the receipt (the
            // window between that commit and SucceedQuietlyAsync below, e.g. PostgreSQL going away in
            // between). The CategorizeReceipt job was inserted in the SAME SaveChangesAsync as the
            // receipt row (EfReceiptStore.SaveExtractedAsync), so its existence needs no separate check
            // here - it is guaranteed by that one atomic commit. Re-extracting would re-download the
            // photo, call the Tax Administration or vision again, and hit a unique-index violation on
            // SaveExtractedAsync for no reason.
            var existingReceipt = await receiptStore.GetByTransactionAsync(job.TransactionId, cancellationToken);
            if (existingReceipt is not null)
            {
                logger.LogReceiptAlreadyExtracted(job.TransactionId);
                if (record.Status == TransactionStatus.Captured)
                {
                    // A replay landing here (C-1) after a save that skipped enqueueing CategorizeReceipt
                    // (2026-09-27) must not claim "Categorising…" when nothing is actually running. Job
                    // existence, not a recomputed sum-vs-total, is the derivation (shared with
                    // RecordActionHandler's Cancel/Restore, and EfTransactionTrace's own trace-page view,
                    // through the same IsAwaitingConfirmationAsync) - a sum-only check could not tell a
                    // malformed-PIB-only pause apart from one already confirmed, since the PIB itself was
                    // never stored once judged invalid (docs/decisions/p6-2-vision-fallback-stopped-inventing-receipts.md).
                    var stillAwaitingConfirmation = await receiptStore.IsAwaitingConfirmationAsync(job.TransactionId, cancellationToken);

                    var echo = stillAwaitingConfirmation
                        ? recordEcho.ComposeReceiptNeedsConfirmation(existingReceipt)
                        : new EchoMessage(recordEcho.ComposeCategorisingReceipt(existingReceipt.Lines.Count), []);

                    await EditQuietlyAsync(notifier, record, echo, cancellationToken);
                }
                await SucceedQuietlyAsync(jobQueue, job, cancellationToken);
                return;
            }

            var telegramFileId = await receiptStore.GetTelegramFileIdAsync(job.TransactionId, cancellationToken);
            var verificationUrl = telegramFileId is null
                ? await receiptStore.GetVerificationUrlAsync(job.TransactionId, cancellationToken)
                : null;

            ReceiptPhoto? photo = null;
            if (telegramFileId is not null)
            {
                var photoSource = scope.ServiceProvider.GetRequiredService<IReceiptPhotoSource>();
                using (timer.Start(logger, TimedOperations.TelegramDownloadFile))
                    photo = await photoSource.DownloadAsync(telegramFileId, cancellationToken);
            }

            var qrUrl = verificationUrl;
            if (qrUrl is null && photo is { } forQr)
            {
                var qrReader = scope.ServiceProvider.GetRequiredService<IQrReader>();
                using var qrStream = new MemoryStream(forQr.Bytes.ToArray());
                qrUrl = qrReader.Read(qrStream);
            }

            ExtractedReceipt extracted;
            var fetchFailed = false;
            var taxIdMalformed = false;
            var kindUnclear = false;
            // Set only on the QR-decoded-but-fetch-failed vision fallback below - the one path where a
            // model can disagree with facts the Tax Administration's own QR already carries offline.
            FiscalQrPayload? verifiedQrFacts = null;

            if (qrUrl is not null)
            {
                var decoder = scope.ServiceProvider.GetRequiredService<IFiscalQrDecoder>();
                var decoded = decoder.Decode(qrUrl);

                if (decoded.Payload is not { } payload)
                {
                    if (photo is { } photoForDecodeFailure)
                    {
                        if (!await EnsureVisionConfiguredAsync(scope, jobQueue, store, notifier, job, record, cancellationToken))
                            return;

                        logger.LogVisionUsed("decode error");
                        if (await ReadWithVisionAsync(
                                scope, jobQueue, store, notifier, job, record, photoForDecodeFailure, qrTotal: null, cancellationToken)
                            is not { } visionResult)
                            return;
                        extracted = visionResult.Receipt;
                        taxIdMalformed = visionResult.TaxIdMalformed;
                        kindUnclear = visionResult.KindUnclear;
                    }
                    else
                    {
                        await FailWithEchoAsync(jobQueue, store, notifier, job, record, recordEcho.NotAFiscalReceiptLink,
                            "the link does not decode as a fiscal receipt", cancellationToken);
                        return;
                    }
                }
                else
                {
                    logger.LogQrDecoded(payload.Total, payload.IssuedAt);
                    var client = scope.ServiceProvider.GetRequiredService<IFiscalReceiptClient>();
                    var fetchResult = await client.FetchAsync(payload, cancellationToken);

                    if (fetchResult.Receipt is { } fetched)
                    {
                        logger.LogReceiptFetched(fetched.Lines.Count, fetched.Total);
                        extracted = fetched;
                    }
                    else
                    {
                        fetchFailed = true;
                        var failure = fetchResult.Failure!;
                        logger.LogReceiptFetchFailed(TransactionStages.ReceiptFetchFailed, failure.Reason, failure.StatusCode);
                        var fetchStatus = scope.ServiceProvider.GetRequiredService<IReceiptFetchStatus>();
                        fetchStatus.RecordFailure(timeProvider.GetUtcNow(), failure.Reason);

                        if (photo is { } photoForFetchFailure)
                        {
                            // N-6 (2026-09-25 re-review): the QR decoded fine, so unlike the two other
                            // vision fallbacks (no QR at all, or a QR that failed to decode) there is
                            // nothing wrong with this capture - the Tax Administration outage may be
                            // transient. Failing terminally under EnsureVisionConfiguredAsync's "no
                            // readable fiscal QR code" message would misdescribe the failure and lose
                            // the receipt for good; retry like any other transient failure instead.
                            var modelProvider = scope.ServiceProvider.GetRequiredService<IModelProvider>();
                            if (!await modelProvider.IsConfiguredAsync(cancellationToken))
                            {
                                throw new ModelCallException(ModelFailureKind.Transient,
                                    "the Tax Administration is unreachable and no AI key is configured for the vision fallback");
                            }

                            logger.LogVisionUsed("fetch failed");
                            verifiedQrFacts = payload;
                            if (await ReadWithVisionAsync(
                                    scope, jobQueue, store, notifier, job, record, photoForFetchFailure, payload.Total, cancellationToken)
                                is not { } visionResult)
                                return;
                            extracted = visionResult.Receipt;
                            taxIdMalformed = visionResult.TaxIdMalformed;
                            kindUnclear = visionResult.KindUnclear;
                        }
                        else
                        {
                            await FailWithEchoAsync(jobQueue, store, notifier, job, record, recordEcho.ReceiptFetchUnreachableLinkOnly,
                                "the Tax Administration is unreachable and there is no photo to fall back on", cancellationToken);
                            return;
                        }
                    }
                }
            }
            else if (photo is { } photoWithNoQr)
            {
                if (!await EnsureVisionConfiguredAsync(scope, jobQueue, store, notifier, job, record, cancellationToken))
                    return;

                logger.LogVisionUsed("no QR");
                if (await ReadWithVisionAsync(scope, jobQueue, store, notifier, job, record, photoWithNoQr, qrTotal: null, cancellationToken)
                    is not { } visionResult)
                    return;
                extracted = visionResult.Receipt;
                taxIdMalformed = visionResult.TaxIdMalformed;
                kindUnclear = visionResult.KindUnclear;
            }
            else
            {
                await FailWithEchoAsync(jobQueue, store, notifier, job, record, recordEcho.NotAFiscalReceiptLink,
                    "the capture has neither a photo nor a verification link", cancellationToken);
                return;
            }

            // Copilot finding, PR #3: a QR-decoded total is a verified fact and always wins. The vision
            // fallback (the only path that can disagree - a fiscal QR/SUF fetch's own Total already comes
            // from the Tax Administration's journal, not a model) must never let its own model-read total
            // survive into what gets saved, logged, or shown as the receipt's total; the model's differing
            // number is discarded here rather than recorded anywhere that could be read back as the total.
            if (extracted.Source == ReceiptSource.Vision && verifiedQrFacts is { } qrFacts)
            {
                if (extracted.Total != qrFacts.Total)
                {
                    logger.LogModelTotalDiscardedForQrTotal(extracted.Total, qrFacts.Total);
                    extracted = extracted with { Total = qrFacts.Total };
                }

                if (extracted.Currency != CurrencyCode.Rsd)
                {
                    logger.LogModelCurrencyDiscardedForQrCurrency(extracted.Currency);
                    extracted = extracted with { Currency = CurrencyCode.Rsd };
                }

                // Copilot finding, PR #3: the decoded QR also carries the authoritative issue time and
                // receipt kind, and composes the printed fiscal number - every one of those is a fact
                // from the Tax Administration's own payload, never the model's guess, so they overwrite
                // the vision answer the same way Total/Currency already do. The seller's tax id has no
                // QR equivalent - it stays vision's own well-formed-only value.
                //
                // The QR's own IssuedAt is a UTC instant; every other producer of ExtractedReceipt.IssuedAt
                // (ChatReceiptVision, FiscalJournalParser, SufReceiptClient) stamps the capture time zone's
                // own offset instead - the convention ComposeReceiptNeedsConfirmationCore's date line relies
                // on, since it prints the value's own offset with no zone conversion. Convert through the
                // same captureTimeZone the duplicate echo below already uses.
                var qrIssuedAt = TimeZoneInfo.ConvertTime(qrFacts.IssuedAt, captureTimeZone);
                if (extracted.IssuedAt != qrIssuedAt)
                {
                    logger.LogModelIssuedAtDiscardedForQrIssuedAt(extracted.IssuedAt, qrIssuedAt);
                    extracted = extracted with { IssuedAt = qrIssuedAt };
                }

                if (extracted.Kind != qrFacts.Kind)
                {
                    logger.LogModelKindDiscardedForQrKind(extracted.Kind, qrFacts.Kind);
                }

                if (extracted.Kind != qrFacts.Kind || kindUnclear)
                {
                    extracted = extracted with { Kind = qrFacts.Kind };
                }

                // The QR's own kind is authoritative, so it resolves the model's own uncertainty too -
                // a receipt no longer needs the operator's confirmation for a kind the Tax
                // Administration itself already named.
                kindUnclear = false;

                if (extracted.FiscalNumber != qrFacts.FiscalNumber)
                {
                    logger.LogModelFiscalNumberDiscardedForQrFiscalNumber(extracted.FiscalNumber, qrFacts.FiscalNumber);
                    extracted = extracted with { FiscalNumber = qrFacts.FiscalNumber };
                }
            }

            var mismatch = HasMismatch(extracted);

            // 2026-09-27 (vision only - a fiscal QR/SUF receipt's own numbers are never second-guessed
            // here): the receipt and its lines are still saved below so the echo can show exactly what
            // was read, but categorisation waits for the operator's own "Record anyway" rather than
            // posting a total or a tax id that might be OCR noise.
            var needsConfirmation = extracted.Source == ReceiptSource.Vision && (mismatch || taxIdMalformed || kindUnclear);

            var saveResult = await receiptStore.SaveExtractedAsync(
                job.TransactionId, extracted, telegramFileId, enqueueCategorization: !needsConfirmation, cancellationToken);

            if (saveResult.DuplicateOfTransactionId is { } duplicateId)
            {
                await recordEditor.CancelAsync(job.TransactionId, cancellationToken);
                logger.LogReceiptDuplicate(duplicateId);
                var duplicateReceipt = await receiptStore.GetByTransactionAsync(duplicateId, cancellationToken);
                // M-6 (2026-09-25 final review): UtcDateTime shifted a receipt issued before 02:00
                // Belgrade onto the previous day. Converted through the capture time zone, as
                // ReceiptCategorizationWorker already computes OccurredOn.
                var duplicateDate = duplicateReceipt?.IssuedAt is { } issuedAt
                    ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(issuedAt, captureTimeZone).DateTime)
                    : (DateOnly?)null;
                // M-11 (Phase 6 final review): the duplicate index ignores status, so the earlier
                // transaction may itself be Cancelled - the only way back is Restore on that message,
                // which the operator cannot otherwise discover.
                var duplicateSubject = await store.GetSubjectAsync(duplicateId, cancellationToken);
                var echo = recordEcho.ComposeReceiptDuplicate(
                    duplicateDate, duplicateReceipt?.Total ?? 0m, duplicateReceipt?.Currency ?? CurrencyCode.Rsd,
                    duplicateSubject?.Status == TransactionStatus.Cancelled);
                await EditQuietlyAsync(notifier, record, echo, cancellationToken);
                await SucceedQuietlyAsync(jobQueue, job, cancellationToken);
                return;
            }

            logger.LogExtracted(TransactionStages.Extracted, extracted.Source, extracted.Lines.Count, extracted.Total,
                extracted.QrTotal, mismatch, fetchFailed);

            if (needsConfirmation)
            {
                logger.LogAwaitingConfirmation(job.TransactionId, mismatch, taxIdMalformed, kindUnclear);
                var echo = recordEcho.ComposeReceiptNeedsConfirmation(extracted, taxIdMalformed, kindUnclear);
                await EditQuietlyAsync(notifier, record, echo, cancellationToken);
            }
            else
            {
                await EditQuietlyAsync(
                    notifier, record, new EchoMessage(recordEcho.ComposeCategorisingReceipt(extracted.Lines.Count), []), cancellationToken);
            }

            await SucceedQuietlyAsync(jobQueue, job, cancellationToken);
        }
        catch (ModelCallException ex) when (ex.IsAccountLevel())
        {
            logger.LogStageFailed(TransactionStages.StageFailed, TransactionStages.Extracted, ex);
            accountCooldownUntil = timeProvider.GetUtcNow() + options.AccountCooldown;
            logger.AccountLevelFailure(job.Id, ex.Message, options.AccountCooldown);
            await HandleModelFailureAsync(jobQueue, store, notifier, job, record, ModelFailureKind.Transient, ex.Message, ex, cancellationToken);
        }
        catch (ModelCallException ex)
        {
            logger.LogStageFailed(TransactionStages.StageFailed, TransactionStages.Extracted, ex);
            await HandleModelFailureAsync(jobQueue, store, notifier, job, record, ex.Kind, ex.Message, ex, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed photo download or QR read lands here too, same as a failed voice download in
            // TranscriptionWorker: worth another attempt, bounded by the attempt cap.
            logger.LogStageFailed(TransactionStages.StageFailed, TransactionStages.Extracted, ex);
            await HandleModelFailureAsync(jobQueue, store, notifier, job, record, ModelFailureKind.Transient, ex.Message, ex, cancellationToken);
        }
    }

    // M-10 (2026-09-25 final review): mirrors ReceiptCategorizationWorker's own gate on
    // IModelProvider.IsConfiguredAsync, but only in front of the vision fallback - unlike that
    // worker, this one's QR + Tax Administration path needs no model at all and must keep working
    // with no key configured. Without this, a QR-less photo burned every retry attempt (each
    // re-downloading the photo) on a vision call that could never succeed.
    async Task<bool> EnsureVisionConfiguredAsync(
        IServiceScope scope, IJobQueue jobQueue, ICategorizationStore store, IChatNotifier notifier,
        CategorizationJob job, CategorizationSubject record, CancellationToken cancellationToken)
    {
        var modelProvider = scope.ServiceProvider.GetRequiredService<IModelProvider>();
        if (await modelProvider.IsConfiguredAsync(cancellationToken))
            return true;

        await FailWithEchoAsync(jobQueue, store, notifier, job, record, recordEcho.ReceiptVisionNotConfigured,
            "no model key is configured for the vision fallback", cancellationToken);
        return false;
    }

    // Returns null when the model itself reported the photo unreadable (or a contradiction this layer
    // treats the same way, ReceiptVisionResult) - the caller's own null check returns without falling
    // through to SaveExtractedAsync. This is a distinct outcome from a ModelCallException: it is the
    // model succeeding at its one job, which is saying it could not read this photo, not a transient or
    // terminal failure of the call itself.
    async Task<(ExtractedReceipt Receipt, bool TaxIdMalformed, bool KindUnclear)?> ReadWithVisionAsync(
        IServiceScope scope, IJobQueue jobQueue, ICategorizationStore store, IChatNotifier notifier,
        CategorizationJob job, CategorizationSubject record, ReceiptPhoto photo, decimal? qrTotal,
        CancellationToken cancellationToken)
    {
        var vision = scope.ServiceProvider.GetRequiredService<IReceiptVision>();
        var scaler = scope.ServiceProvider.GetRequiredService<IReceiptImageScaler>();
        var scaled = scaler.ScaleForVision(photo);
        var result = await vision.ReadAsync(scaled.Bytes, scaled.MediaType, qrTotal, cancellationToken);

        if (result.Unreadable is { } reason)
        {
            await FailWithEchoAsync(jobQueue, store, notifier, job, record, recordEcho.ReceiptUnreadable,
                $"the receipt photo was not readable ({reason})", cancellationToken);
            return null;
        }

        return (result.Receipt!, result.SellerTaxIdMalformed, result.KindUnclear);
    }

    async Task FailWithEchoAsync(
        IJobQueue jobQueue, ICategorizationStore store, IChatNotifier notifier, CategorizationJob job,
        CategorizationSubject record, EchoMessage echo, string error, CancellationToken cancellationToken)
    {
        logger.LogStageFailed(TransactionStages.StageFailed, TransactionStages.Extracted, new InvalidOperationException(error));

        if (await jobQueue.FailAsync(job.Id, workerId, error, cancellationToken) != JobCompletionOutcome.Applied)
            return;

        await store.MarkFailedAsync(job.TransactionId, RecordFailureReason.None, cancellationToken);
        await EditQuietlyAsync(notifier, record, echo, cancellationToken);
    }

    async Task HandleModelFailureAsync(
        IJobQueue jobQueue, ICategorizationStore store, IChatNotifier notifier, CategorizationJob job,
        CategorizationSubject? record, ModelFailureKind kind, string error, Exception exception, CancellationToken cancellationToken)
    {
        if (kind == ModelFailureKind.Terminal)
        {
            if (await jobQueue.FailAsync(job.Id, workerId, error, cancellationToken) == JobCompletionOutcome.Applied)
                await ReportFailureAsync(store, notifier, job, record, cancellationToken);
            return;
        }

        var isLastAttempt = job.AttemptCount >= options.MaxAttempts;
        var runAfter = timeProvider.GetUtcNow() + options.ComputeBackoff(job.AttemptCount);
        var outcome = await jobQueue.RetryAsync(job.Id, workerId, runAfter, error, cancellationToken);

        if (outcome != JobCompletionOutcome.Applied)
            return;

        if (isLastAttempt)
        {
            await ReportFailureAsync(store, notifier, job, record, cancellationToken);
            return;
        }

        if (record is null)
            return;

        var localRunAfter = TimeZoneInfo.ConvertTime(runAfter, captureTimeZone);
        var notice = recordEcho.ComposeReceiptExtractionRetryNotice(exception, localRunAfter);
        await EditQuietlyAsync(notifier, record, notice, cancellationToken);
    }

    async Task ReportFailureAsync(
        ICategorizationStore store, IChatNotifier notifier, CategorizationJob job, CategorizationSubject? record,
        CancellationToken cancellationToken)
    {
        await store.MarkFailedAsync(job.TransactionId, RecordFailureReason.None, cancellationToken);

        if (record is null)
            return;

        await EditQuietlyAsync(notifier, record, recordEcho.ReceiptReadFailure, cancellationToken);
    }

    async Task EditQuietlyAsync(IChatNotifier notifier, CategorizationSubject record, EchoMessage echo, CancellationToken cancellationToken)
    {
        if (record.BotMessageId is not { } messageId)
            return;

        try
        {
            await notifier.EditAsync(record.TelegramChatId, messageId, echo, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.EditFailed(ex, messageId, record.TransactionId);
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
            logger.SucceedAfterHandOffFailed(ex, job.Id);
        }
    }

    // This worker's own gate for whether a fresh vision read needs the operator's "Record anyway"
    // (docs/decisions/p6-2-vision-fallback-stopped-inventing-receipts.md) - kept private here rather than shared, since RecordEcho's
    // ComposeReceiptNeedsConfirmation recomputes the identical one-line arithmetic itself for its own
    // wording; there is nothing to drift between two independent decimal comparisons this small.
    static bool HasMismatch(ExtractedReceipt extracted) =>
        Math.Abs(extracted.Lines.Sum(line => line.Total) - (extracted.QrTotal ?? extracted.Total)) > 0.01m;
}
