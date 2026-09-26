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
// every degraded step visible on the trace (docs/superpowers/specs/2026-09-25-receipts-design.md §2).
internal sealed class ExtractReceiptWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    CategorizationWorkerOptions options,
    string workerId,
    IRecordEcho recordEcho,
    IDatabaseGate gate,
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
            await jobQueue.ReleaseExpiredLeasesAsync(now, cancellationToken);

            if (now < accountCooldownUntil)
                return CategorizationTickResult.Idle;

            var job = await jobQueue.ClaimAsync(workerId, ClaimableKinds, options.Lease, cancellationToken);
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
                    await EditQuietlyAsync(notifier, record,
                        new EchoMessage(recordEcho.ComposeCategorisingReceipt(existingReceipt.Lines.Count), []), cancellationToken);
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

            if (qrUrl is not null)
            {
                var decoder = scope.ServiceProvider.GetRequiredService<IFiscalQrDecoder>();
                var decoded = decoder.Decode(qrUrl);

                if (decoded.Payload is not { } payload)
                {
                    if (photo is { } photoForDecodeFailure)
                    {
                        logger.LogVisionUsed("decode error");
                        extracted = await ReadWithVisionAsync(scope, photoForDecodeFailure, qrTotal: null, cancellationToken);
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
                            logger.LogVisionUsed("fetch failed");
                            extracted = await ReadWithVisionAsync(scope, photoForFetchFailure, payload.Total, cancellationToken);
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
                logger.LogVisionUsed("no QR");
                extracted = await ReadWithVisionAsync(scope, photoWithNoQr, qrTotal: null, cancellationToken);
            }
            else
            {
                await FailWithEchoAsync(jobQueue, store, notifier, job, record, recordEcho.NotAFiscalReceiptLink,
                    "the capture has neither a photo nor a verification link", cancellationToken);
                return;
            }

            var saveResult = await receiptStore.SaveExtractedAsync(job.TransactionId, extracted, telegramFileId, cancellationToken);

            if (saveResult.DuplicateOfTransactionId is { } duplicateId)
            {
                await recordEditor.CancelAsync(job.TransactionId, cancellationToken);
                logger.LogReceiptDuplicate(duplicateId);
                var duplicateReceipt = await receiptStore.GetByTransactionAsync(duplicateId, cancellationToken);
                var duplicateDate = duplicateReceipt?.IssuedAt is { } issuedAt ? DateOnly.FromDateTime(issuedAt.UtcDateTime) : (DateOnly?)null;
                var echo = recordEcho.ComposeReceiptDuplicate(
                    duplicateDate, duplicateReceipt?.Total ?? 0m, duplicateReceipt?.Currency ?? CurrencyCode.Rsd);
                await EditQuietlyAsync(notifier, record, echo, cancellationToken);
                await SucceedQuietlyAsync(jobQueue, job, cancellationToken);
                return;
            }

            var mismatch = Math.Abs(extracted.Lines.Sum(line => line.Total) - (extracted.QrTotal ?? extracted.Total)) > 0.01m;
            logger.LogExtracted(TransactionStages.Extracted, extracted.Source, extracted.Lines.Count, extracted.Total,
                extracted.QrTotal, mismatch, fetchFailed);

            await EditQuietlyAsync(
                notifier, record, new EchoMessage(recordEcho.ComposeCategorisingReceipt(extracted.Lines.Count), []), cancellationToken);
            await SucceedQuietlyAsync(jobQueue, job, cancellationToken);
        }
        catch (ModelCallException ex) when (ex.IsAccountLevel())
        {
            logger.LogStageFailed(TransactionStages.StageFailed, TransactionStages.Extracted, ex);
            accountCooldownUntil = timeProvider.GetUtcNow() + options.AccountCooldown;
            logger.AccountLevelFailure(job.Id, ex.Message, options.AccountCooldown);
            await HandleModelFailureAsync(jobQueue, store, notifier, job, record, ModelFailureKind.Transient, ex.Message, cancellationToken);
        }
        catch (ModelCallException ex)
        {
            logger.LogStageFailed(TransactionStages.StageFailed, TransactionStages.Extracted, ex);
            await HandleModelFailureAsync(jobQueue, store, notifier, job, record, ex.Kind, ex.Message, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed photo download or QR read lands here too, same as a failed voice download in
            // TranscriptionWorker: worth another attempt, bounded by the attempt cap.
            logger.LogStageFailed(TransactionStages.StageFailed, TransactionStages.Extracted, ex);
            await HandleModelFailureAsync(jobQueue, store, notifier, job, record, ModelFailureKind.Transient, ex.Message, cancellationToken);
        }
    }

    async Task<ExtractedReceipt> ReadWithVisionAsync(
        IServiceScope scope, ReceiptPhoto photo, decimal? qrTotal, CancellationToken cancellationToken)
    {
        var vision = scope.ServiceProvider.GetRequiredService<IReceiptVision>();
        return await vision.ReadAsync(photo.Bytes, photo.MediaType, qrTotal, cancellationToken);
    }

    async Task FailWithEchoAsync(
        IJobQueue jobQueue, ICategorizationStore store, IChatNotifier notifier, CategorizationJob job,
        CategorizationSubject record, EchoMessage echo, string error, CancellationToken cancellationToken)
    {
        logger.LogStageFailed(TransactionStages.StageFailed, TransactionStages.Extracted, new InvalidOperationException(error));

        if (await jobQueue.FailAsync(job.Id, workerId, error, cancellationToken) != JobCompletionOutcome.Applied)
            return;

        await store.MarkFailedAsync(job.TransactionId, cancellationToken);
        await EditQuietlyAsync(notifier, record, echo, cancellationToken);
    }

    async Task HandleModelFailureAsync(
        IJobQueue jobQueue, ICategorizationStore store, IChatNotifier notifier, CategorizationJob job,
        CategorizationSubject? record, ModelFailureKind kind, string error, CancellationToken cancellationToken)
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

        if (outcome == JobCompletionOutcome.Applied && isLastAttempt)
            await ReportFailureAsync(store, notifier, job, record, cancellationToken);
    }

    async Task ReportFailureAsync(
        ICategorizationStore store, IChatNotifier notifier, CategorizationJob job, CategorizationSubject? record,
        CancellationToken cancellationToken)
    {
        await store.MarkFailedAsync(job.TransactionId, cancellationToken);

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
}
