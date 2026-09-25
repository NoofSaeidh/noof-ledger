using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Jobs;
using Noof.Ledger.Application.Transcription;
using Noof.Ledger.Domain;
using Noof.Ledger.Host.Workers.TranscriptionLogging;

namespace Noof.Ledger.Host.Workers;

// Claims only Transcribe jobs and hands each transcript to the ordinary pipeline (V4, V8). It shares
// CategorizationWorkerOptions on purpose: its attempt cap must be the one EfJobQueue enforces, and there is one.
internal sealed class TranscriptionWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    CategorizationWorkerOptions options,
    string workerId,
    IRecordEcho recordEcho,
    IDatabaseGate gate,
    ILogger<TranscriptionWorker> logger)
    : BackgroundService
{
    static readonly JobKind[] ClaimableKinds = [JobKind.Transcribe];

    // In memory and per instance, for CategorizationWorker's reasons; a broken speech key pauses only this worker.
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
            var speechProvider = scope.ServiceProvider.GetRequiredService<ISpeechProvider>();

            var now = timeProvider.GetUtcNow();
            await jobQueue.ReleaseExpiredLeasesAsync(now, cancellationToken);

            // Both checked before claiming, for CategorizationWorker's reason: a claim spends an attempt and nothing
            // gives it back.
            if (!await speechProvider.IsConfiguredAsync(cancellationToken))
                return CategorizationTickResult.Idle;

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
        var notifier = scope.ServiceProvider.GetRequiredService<IChatNotifier>();

        CategorizationSubject? record = null;

        using var logScope = TransactionLogScope.Begin(logger, job.TransactionId);

        try
        {
            record = await store.GetSubjectAsync(job.TransactionId, cancellationToken);
            if (record is null)
            {
                logger.LogStageFailed(TransactionStages.StageFailed, TransactionStages.Transcribed,
                    new InvalidOperationException("the transaction this job points at no longer exists"));
                await jobQueue.FailAsync(job.Id, workerId, "the transaction this job points at no longer exists", cancellationToken);
                return;
            }

            var voiceFiles = scope.ServiceProvider.GetRequiredService<IVoiceFileSource>();
            var transcriber = scope.ServiceProvider.GetRequiredService<ITranscriber>();

            // A check constraint holds voice_file_id on every Transcribe job.
            await using var audio = await voiceFiles.DownloadAsync(job.VoiceFileId!, cancellationToken);
            var transcriptionStartedAt = timeProvider.GetUtcNow();
            var transcript = await transcriber.TranscribeAsync(audio, cancellationToken);
            var transcriptionDuration = timeProvider.GetUtcNow() - transcriptionStartedAt;

            if (transcript.Length == 0)
            {
                logger.LogStageFailed(TransactionStages.StageFailed, TransactionStages.Transcribed,
                    new InvalidOperationException("nothing was heard in the voice note"));
                await ReportNothingHeardAsync(jobQueue, store, notifier, job, record, cancellationToken);
                return;
            }

            logger.LogTranscribed(TransactionStages.Transcribed, transcriptionDuration.TotalSeconds, transcript.Length);

            var transcriptionStore = scope.ServiceProvider.GetRequiredService<ITranscriptionStore>();
            var handedOn = job.SourceMessageId is { } sourceMessageId
                ? await transcriptionStore.CompleteCorrectionAsync(
                    job.TransactionId, transcript, sourceMessageId, job.InstructionDay, cancellationToken)
                : await transcriptionStore.CompleteCaptureAsync(job.TransactionId, transcript, cancellationToken);

            if (!handedOn)
                logger.TranscriptAlreadyHandedOn(job.Id);

            // The hand-off is committed. As in CategorizationWorker, nothing past this line may count as the job
            // failing: that would mark Failed a record whose reading is already queued.
            await SucceedQuietlyAsync(jobQueue, job, cancellationToken);
        }
        catch (ModelCallException ex) when (ex.IsAccountLevel())
        {
            // 401/402/403: the key, not this note, is what is broken. Retried, never failed, and claiming pauses so the
            // backlog is not burned through while the key stays bad.
            logger.LogStageFailed(TransactionStages.StageFailed, TransactionStages.Transcribed, ex);
            accountCooldownUntil = timeProvider.GetUtcNow() + options.AccountCooldown;
            logger.AccountLevelFailure(job.Id, ex.Message, options.AccountCooldown);
            await HandleFailureAsync(jobQueue, store, notifier, job, record, ModelFailureKind.Transient, ex.Message, cancellationToken);
        }
        catch (ModelCallException ex)
        {
            logger.LogStageFailed(TransactionStages.StageFailed, TransactionStages.Transcribed, ex);
            await HandleFailureAsync(jobQueue, store, notifier, job, record, ex.Kind, ex.Message, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed download lands here and is worth another attempt (V9); the attempt cap bounds everything else.
            logger.LogStageFailed(TransactionStages.StageFailed, TransactionStages.Transcribed, ex);
            await HandleFailureAsync(jobQueue, store, notifier, job, record, ModelFailureKind.Transient, ex.Message, cancellationToken);
        }
    }

    async Task ReportNothingHeardAsync(
        IJobQueue jobQueue, ICategorizationStore store, IChatNotifier notifier, CategorizationJob job,
        CategorizationSubject record, CancellationToken cancellationToken)
    {
        if (await jobQueue.FailAsync(job.Id, workerId, "nothing was heard in the voice note", cancellationToken)
            != JobCompletionOutcome.Applied)
            return;

        // A spoken correction that said nothing leaves the record exactly as it was (V6).
        if (IsCorrection(job))
        {
            await EditQuietlyAsync(notifier, record, recordEcho.ComposeHeardNothing(record), cancellationToken);
            return;
        }

        await store.MarkFailedAsync(job.TransactionId, cancellationToken);
        await EditQuietlyAsync(notifier, record, recordEcho.HeardNothing, cancellationToken);
    }

    async Task HandleFailureAsync(
        IJobQueue jobQueue, ICategorizationStore store, IChatNotifier notifier, CategorizationJob job,
        CategorizationSubject? record, ModelFailureKind kind, string error, CancellationToken cancellationToken)
    {
        if (kind == ModelFailureKind.Terminal)
        {
            if (await jobQueue.FailAsync(job.Id, workerId, error, cancellationToken) == JobCompletionOutcome.Applied)
                await ReportFailureAsync(store, notifier, job, record, cancellationToken);
            return;
        }

        // The predicate EfJobQueue.RetryAsync evaluates; the two agree because both read the one MaxAttempts.
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
        // Only a voice capture is marked Failed; a failed spoken correction leaves the record as the person saw it.
        if (!IsCorrection(job))
            await store.MarkFailedAsync(job.TransactionId, cancellationToken);

        if (record is null)
            return;

        var echo = IsCorrection(job) ? recordEcho.ComposeCorrectionFailure(record) : recordEcho.TranscriptionFailure;
        await EditQuietlyAsync(notifier, record, echo, cancellationToken);
    }

    // A capture's transcription has no source message; a spoken correction's is the reply it arrived in.
    static bool IsCorrection(CategorizationJob job) => job.SourceMessageId is not null;

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
