using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Jobs;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Host.Workers;

internal enum CategorizationTickResult { Idle, Processed, Failed }

internal sealed class CategorizationWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    CategorizationWorkerOptions options,
    string workerId,
    ILogger<CategorizationWorker> logger)
    : BackgroundService
{
    // In-memory, per worker-instance state: this host process's own window of "an account-level
    // failure just happened, do not claim more work yet." Deliberately not persisted - a second
    // host process backs off independently the same way, and a restart clears it, which is fine
    // because a restart means a fresh attempt is exactly what should happen.
    DateTimeOffset accountCooldownUntil = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
            var secretStore = scope.ServiceProvider.GetRequiredService<ISecretStore>();

            var now = timeProvider.GetUtcNow();
            await jobQueue.ReleaseExpiredLeasesAsync(now, cancellationToken);

            // Checked BEFORE claiming, deliberately out of the order the plan's flow diagram shows.
            // ClaimAsync increments attempt_count as part of the same UPDATE that claims the row, and
            // no verb undoes only that increment - claiming first and releasing on a missing key would
            // burn one of eight attempts on every tick until the operator pastes a key, and every job
            // captured before then would be permanently Failed within minutes.
            var keyStatus = await secretStore.GetStatusAsync(SecretKeys.AnthropicApiKey, cancellationToken);
            if (keyStatus.State is not SecretState.Present)
                return CategorizationTickResult.Idle;

            // Same reasoning, same placement, as the key-presence check above: checked before
            // claiming, so a cooldown never burns an attempt on the next job in line either.
            if (now < accountCooldownUntil)
                return CategorizationTickResult.Idle;

            var job = await jobQueue.ClaimAsync(workerId, options.Lease, cancellationToken);
            if (job is null)
                return CategorizationTickResult.Idle;

            await ProcessClaimedJobAsync(scope, jobQueue, job, cancellationToken);
            return CategorizationTickResult.Processed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Categorization worker tick failed");
            return CategorizationTickResult.Failed;
        }
    }

    async Task ProcessClaimedJobAsync(
        IServiceScope scope, IJobQueue jobQueue, CategorizationJob job, CancellationToken cancellationToken)
    {
        var store = scope.ServiceProvider.GetRequiredService<ICategorizationStore>();
        var categoryCatalog = scope.ServiceProvider.GetRequiredService<ICategoryCatalog>();
        var merchantDirectory = scope.ServiceProvider.GetRequiredService<IMerchantDirectory>();
        var categorizer = scope.ServiceProvider.GetRequiredService<ICategorizer>();
        var notifier = scope.ServiceProvider.GetRequiredService<IChatNotifier>();

        CategorizationSubject? subject = null;

        try
        {
            subject = await store.GetSubjectAsync(job.TransactionId, cancellationToken);
            if (subject is not { } sub)
            {
                await FailTerminallyAsync(
                    jobQueue, store, notifier, job, null,
                    "the transaction this job points at no longer exists", cancellationToken);
                return;
            }

            var categories = await categoryCatalog.ActiveAsync(cancellationToken);
            var aliases = await merchantDirectory.AliasesAsync(cancellationToken);
            var hints = MerchantScan.Matches(sub.RawText, aliases, options.MerchantHintLimit)
                .DistinctBy(alias => alias.MerchantId)
                .Select(alias => new MerchantOption(alias.MerchantId, alias.DisplayName))
                .ToList();

            var allMerchants = await merchantDirectory.MerchantsAsync(cancellationToken);

            var request = new CategorizationRequest(
                sub.RawText,
                categories.Select(category => new CategoryOption(category.Slug, category.NameEn, category.NameRu, category.ParentSlug)).ToList(),
                hints,
                allMerchants);

            var proposal = await categorizer.ProposeAsync(request, cancellationToken);

            var offeredSlugs = categories.Select(category => category.Slug).ToHashSet();

            // The full directory, NOT just the hints. A model that called list_merchants answers with
            // an id it learned there, and validating against the short hint list would reject exactly
            // the answers that tool exists to produce - the tool would appear to work and every result
            // it influenced would fail verification.
            var offeredMerchantIds = allMerchants.Select(merchant => merchant.Id).ToHashSet();

            if (!ProposalMapper.TryMap(
                proposal, offeredSlugs, offeredMerchantIds, options.DefaultCurrency, out var mapped, out var failure))
            {
                await FailTerminallyAsync(jobQueue, store, notifier, job, subject, failure, cancellationToken);
                return;
            }

            var categoryNameBySlug = categories.ToDictionary(category => category.Slug, category => category.NameEn);
            var aliasByFolded = aliases.ToDictionary(alias => alias.Folded, alias => alias);
            var canonicalizations = 0;

            var categorizedItems = new List<CategorizedLineItem>(mapped.Items.Count);
            var replyLines = new List<CategorizationReply.ReplyLine>(mapped.Items.Count);

            foreach (var item in mapped.Items)
            {
                var merchantId = item.KnownMerchantId;

                if (merchantId is null && item.MerchantName is { Length: > 0 } merchantText)
                {
                    var folded = MerchantName.Fold(merchantText);

                    if (aliasByFolded.TryGetValue(folded, out var existingAlias))
                    {
                        merchantId = existingAlias.MerchantId;
                    }
                    else if (canonicalizations < options.MaxCanonicalizationsPerJob)
                    {
                        // allMerchants was already fetched for the request - not re-read here.
                        var displayName = await categorizer.CanonicalizeMerchantAsync(merchantText, allMerchants, cancellationToken);
                        var linkedId = await merchantDirectory.LinkAliasAsync(folded, displayName, cancellationToken);

                        // Written back locally so a second line naming the same new merchant hits
                        // the dictionary instead of paying for a second canonicalisation call.
                        aliasByFolded[folded] = new MerchantAliasEntry(folded, linkedId, displayName);
                        merchantId = linkedId;
                        canonicalizations++;
                    }
                    else
                    {
                        // Past the cap the line keeps its amount and category and simply has no
                        // merchant. Failing the job instead would throw away a correctly extracted
                        // bill over a field that is decoration, and the alias table stays clean.
                        logger.LogInformation(
                            "Job {JobId} reached the canonicalization cap of {Cap}; '{MerchantText}' was left unlinked",
                            job.Id, options.MaxCanonicalizationsPerJob, merchantText);
                    }
                }

                var categoryId = categories.First(category => category.Slug == item.CategorySlug).Id;
                categorizedItems.Add(new CategorizedLineItem(item.Description, item.Amount, categoryId, merchantId));
                replyLines.Add(new CategorizationReply.ReplyLine(item.Description, item.Amount, categoryNameBySlug[item.CategorySlug]));
            }

            await store.ApplyAsync(job.TransactionId, new CategorizationOutcome(categorizedItems, sub.OccurredOn), cancellationToken);

            // From this line on, the transaction's line items and Completed status are already
            // committed. Nothing past here may ever be treated as a job failure - that would run
            // FailTerminallyAsync/HandleModelFailureAsync's last-attempt path and call
            // store.MarkFailedAsync, flipping a Completed transaction back to Failed while its
            // already-written line items stay in the table: the dashboard would then say nothing
            // was recorded for that message while the month totals still counted it. A dropped
            // Telegram edit or a SucceedAsync blip past this point is cosmetic to the job queue
            // bookkeeping, not a reason to touch the transaction again, so both are caught locally
            // instead of being allowed to reach the outer catch blocks below - each independently,
            // so a dropped edit never prevents the SucceedAsync attempt that follows it.
            if (sub.BotMessageId is { } messageId)
            {
                try
                {
                    var text = replyLines.Count == 0
                        ? CategorizationReply.ComposeNothingToRecord(sub.WalletName)
                        : CategorizationReply.ComposeSuccess(sub.WalletName, replyLines);
                    await notifier.EditAsync(sub.TelegramChatId, messageId, text, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex,
                        "Failed to edit Telegram message {MessageId} for job {JobId}; the categorization itself already succeeded",
                        messageId, job.Id);
                }
            }

            try
            {
                var succeedOutcome = await jobQueue.SucceedAsync(job.Id, workerId, cancellationToken);
                if (succeedOutcome == JobCompletionOutcome.NotOwned)
                    logger.LogWarning("Job {JobId} was already reclaimed by another worker; not retrying", job.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "SucceedAsync failed for job {JobId} after its line items were already committed; the transaction is left as Completed",
                    job.Id);
            }
        }
        catch (ModelCallException ex) when (ex.IsAccountLevel())
        {
            // 401/402/403: the account, not this job's request, is what's broken - every other
            // queued job would fail identically against it. Handled as Transient regardless of
            // ex.Kind (Terminal, per the locked status table) so this one occurrence cannot
            // permanently fail the job it happened to land on, and claiming pauses for a cooldown
            // so the rest of the backlog is not burned through while the key stays bad.
            accountCooldownUntil = timeProvider.GetUtcNow() + options.AccountCooldown;
            logger.LogWarning(
                "Account-level Anthropic failure on job {JobId} ({Message}); pausing new claims for {Cooldown}",
                job.Id, ex.Message, options.AccountCooldown);
            await HandleModelFailureAsync(jobQueue, store, notifier, job, subject, ModelFailureKind.Transient, ex.Message, cancellationToken);
        }
        catch (ModelCallException ex)
        {
            await HandleModelFailureAsync(jobQueue, store, notifier, job, subject, ex.Kind, ex.Message, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anything unmodeled here - a bug, an unexpected EF failure - is treated as Transient. The
            // attempt cap already bounds the damage: a persistent failure converges to Failed after
            // MaxAttempts instead of leaving the job Claimed for a full lease duration for no reason.
            await HandleModelFailureAsync(jobQueue, store, notifier, job, subject, ModelFailureKind.Transient, ex.Message, cancellationToken);
        }
    }

    async Task HandleModelFailureAsync(
        IJobQueue jobQueue, ICategorizationStore store, IChatNotifier notifier,
        CategorizationJob job, CategorizationSubject? subject, ModelFailureKind kind, string error,
        CancellationToken cancellationToken)
    {
        if (kind == ModelFailureKind.Terminal)
        {
            await FailTerminallyAsync(jobQueue, store, notifier, job, subject, error, cancellationToken);
            return;
        }

        // The same predicate EfJobQueue.RetryAsync evaluates server-side - see decision 10. This only
        // stays correct because Program.cs feeds EfJobQueue the same CategorizationWorkerOptions.MaxAttempts.
        var isLastAttempt = job.AttemptCount >= options.MaxAttempts;
        var runAfter = timeProvider.GetUtcNow() + options.ComputeBackoff(job.AttemptCount);
        var outcome = await jobQueue.RetryAsync(job.Id, workerId, runAfter, error, cancellationToken);

        if (outcome == JobCompletionOutcome.Applied && isLastAttempt)
            await NotifyFailureAsync(store, notifier, subject, job.TransactionId, cancellationToken);
    }

    async Task FailTerminallyAsync(
        IJobQueue jobQueue, ICategorizationStore store, IChatNotifier notifier,
        CategorizationJob job, CategorizationSubject? subject, string error, CancellationToken cancellationToken)
    {
        var outcome = await jobQueue.FailAsync(job.Id, workerId, error, cancellationToken);
        if (outcome == JobCompletionOutcome.Applied)
            await NotifyFailureAsync(store, notifier, subject, job.TransactionId, cancellationToken);
    }

    async Task NotifyFailureAsync(
        ICategorizationStore store, IChatNotifier notifier, CategorizationSubject? subject, Guid transactionId,
        CancellationToken cancellationToken)
    {
        await store.MarkFailedAsync(transactionId, cancellationToken);

        if (subject is not { BotMessageId: { } messageId } sub)
            return;

        try
        {
            var text = CategorizationReply.ComposeFailure(sub.WalletName);
            await notifier.EditAsync(sub.TelegramChatId, messageId, text, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Failed to edit Telegram message {MessageId} to report a failed categorization for transaction {TransactionId}",
                messageId, transactionId);
        }
    }

    public static string CreateWorkerId()
    {
        // claimed_by is varchar(128). 64 (machine, capped) + 1 + up to 10 digits (a 32-bit process id,
        // worst case) + 1 + 8 (random suffix) = 84 characters, worst case - well inside the column. The
        // random suffix exists because a machine name plus a process id alone is not unique across a
        // crash-and-immediately-restart cycle, where the OS can recycle a process id fast enough for
        // two worker generations to collide inside the same lease window.
        var machine = Environment.MachineName.Length > 64 ? Environment.MachineName[..64] : Environment.MachineName;
        var suffix = Guid.NewGuid().ToString("N")[..8];

        return $"{machine}-{Environment.ProcessId}-{suffix}";
    }
}
