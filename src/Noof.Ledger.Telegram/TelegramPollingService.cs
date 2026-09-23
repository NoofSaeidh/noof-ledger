using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Secrets;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Noof.Ledger.Telegram;

internal enum TelegramPollResult { Idle, Processed, Failed }

internal sealed class TelegramPollingService(
    IServiceScopeFactory scopeFactory,
    ITelegramBotClientFactory clientFactory,
    TelegramClientHandle clientHandle,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<TelegramPollingService> logger)
    : BackgroundService
{
    const string PoisonUpdateNotice =
        "Не смог обработать это сообщение после нескольких попыток — пропускаю его, чтобы не задерживать следующие.";

    static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(5);
    const int MaxUpdateAttempts = 3;

    string? activeToken;
    int? offset;
    int consecutiveFailures;
    int? poisonUpdateId;
    int poisonUpdateAttempts;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var result = await RunTickAsync(stoppingToken);

            var delay = result switch
            {
                TelegramPollResult.Idle => IdlePollInterval,
                TelegramPollResult.Failed => TelegramBackoff.Compute(consecutiveFailures),
                _ => TimeSpan.Zero,
            };

            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, timeProvider, stoppingToken);
        }
    }

    public async Task<TelegramPollResult> RunTickAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var secretStore = scope.ServiceProvider.GetRequiredService<ISecretStore>();

            var secret = await secretStore.GetAsync(SecretKeys.TelegramBotToken, cancellationToken);

            if (secret.State is not SecretState.Present)
                return TelegramPollResult.Idle;

            if (secret.Value != activeToken)
            {
                var client = clientFactory.Create(secret.Value!);
                await client.DeleteWebhook(cancellationToken: cancellationToken);
                clientHandle.Current = client;
                activeToken = secret.Value;

                var offsetStore = scope.ServiceProvider.GetRequiredService<TelegramUpdateOffsetStore>();
                offset ??= await offsetStore.GetAsync(cancellationToken);
            }

            var timeZoneId = configuration["Capture:TimeZone"] ?? "Europe/Belgrade";
            var pollingSeconds = int.TryParse(configuration["Telegram:PollingSeconds"], out var seconds) ? seconds : 30;

            // clientHandle.Current is always assigned above whenever secret.Value != activeToken,
            // and that branch is guaranteed to have run at least once by this point: activeToken
            // starts null and secret.State is Present here, so the very first successful tick sets it
            // before this line is ever reached.
            var updates = await clientHandle.Current!.GetUpdates(
                offset: offset,
                timeout: pollingSeconds,
                allowedUpdates: [UpdateType.Message, UpdateType.EditedMessage, UpdateType.CallbackQuery],
                cancellationToken: cancellationToken);

            if (updates.Length > 0)
            {
                var router = scope.ServiceProvider.GetRequiredService<ITelegramUpdateRouter>();
                var offsetStore = scope.ServiceProvider.GetRequiredService<TelegramUpdateOffsetStore>();

                foreach (var update in updates)
                {
                    try
                    {
                        await router.HandleAsync(update, timeZoneId, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        if (poisonUpdateId != update.Id)
                        {
                            poisonUpdateId = update.Id;
                            poisonUpdateAttempts = 0;
                        }
                        poisonUpdateAttempts++;

                        logger.LogError(ex, "Telegram update {UpdateId} failed on attempt {Attempt}/{MaxAttempts}",
                            update.Id, poisonUpdateAttempts, MaxUpdateAttempts);

                        if (poisonUpdateAttempts < MaxUpdateAttempts)
                        {
                            // Leave the offset where it is so this same update is retried on the
                            // next tick, and stop here - anything behind it waits for its turn,
                            // the same as it always has, until the attempt budget runs out.
                            consecutiveFailures++;
                            return TelegramPollResult.Failed;
                        }

                        logger.LogError(
                            "Telegram update {UpdateId} failed {MaxAttempts} times; skipping it so later updates aren't blocked behind it",
                            update.Id, MaxUpdateAttempts);
                        await NotifyOperatorOfSkippedUpdateAsync(scope, update, cancellationToken);
                        poisonUpdateId = null;
                        poisonUpdateAttempts = 0;
                    }

                    // Reached both when HandleAsync succeeds and when it has just been given up on
                    // as poison - either way this update is done with, and the offset moves past
                    // it. Advancing past a poison update trades that one lost message for a queue
                    // that keeps working; Telegram itself discards unconfirmed updates after 24
                    // hours, so leaving the offset here forever loses every later message too.
                    offset = update.Id + 1;
                    await offsetStore.SetAsync(offset.Value, cancellationToken);
                }
            }

            consecutiveFailures = 0;
            return TelegramPollResult.Processed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            consecutiveFailures++;
            logger.LogError(ex, "Telegram poll tick failed; backing off and retrying");
            return TelegramPollResult.Failed;
        }
    }

    async Task NotifyOperatorOfSkippedUpdateAsync(IServiceScope scope, Update update, CancellationToken cancellationToken)
    {
        if ((update.Message ?? update.EditedMessage ?? update.CallbackQuery?.Message) is not { } message)
            return;

        try
        {
            var chatNotifier = scope.ServiceProvider.GetRequiredService<IChatNotifier>();
            await chatNotifier.SendAsync(message.Chat.Id, PoisonUpdateNotice, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to notify the operator that Telegram update {UpdateId} was skipped", update.Id);
        }
    }
}
