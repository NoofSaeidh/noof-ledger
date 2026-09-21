using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Noof.Ledger.Application.Secrets;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace Noof.Ledger.Telegram;

public enum TelegramPollResult { Idle, Processed, Failed }

public sealed class TelegramPollingService(
    IServiceScopeFactory scopeFactory,
    ITelegramBotClientFactory clientFactory,
    TelegramClientHandle clientHandle,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<TelegramPollingService> logger)
    : BackgroundService
{
    static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(5);

    string? activeToken;
    int? offset;
    int consecutiveFailures;

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
                allowedUpdates: [UpdateType.Message],
                cancellationToken: cancellationToken);

            if (updates.Length > 0)
            {
                var router = scope.ServiceProvider.GetRequiredService<ITelegramUpdateRouter>();
                var offsetStore = scope.ServiceProvider.GetRequiredService<TelegramUpdateOffsetStore>();

                foreach (var update in updates)
                {
                    await router.HandleAsync(update, timeZoneId, cancellationToken);
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
            logger.LogError(ex, "Telegram getUpdates failed; backing off and retrying");
            return TelegramPollResult.Failed;
        }
    }
}
