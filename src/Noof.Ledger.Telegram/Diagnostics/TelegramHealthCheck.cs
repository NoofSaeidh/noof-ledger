using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Telegram.Diagnostics;

internal sealed class TelegramHealthCheck(
    IDatabaseGate gate, ISecretStore secretStore, IPollingHeartbeat heartbeat, TimeProvider timeProvider) : ISystemHealthCheck
{
    static readonly TimeSpan FreshWindow = TimeSpan.FromMinutes(2);

    public string Name => "Telegram";

    public int Order => 30;

    public string LogCategory => "Noof.Ledger.Telegram";

    public async Task<HealthOutcome> CheckAsync(CancellationToken cancellationToken)
    {
        if (gate.State is not DatabaseState.Ready)
            return HealthOutcome.Warning("Waiting for the database");

        var token = await secretStore.GetStatusAsync(SecretKeys.TelegramBotToken, cancellationToken);
        if (token.State is not SecretState.Present)
            return HealthOutcome.Failing("No Telegram bot token configured");

        var now = timeProvider.GetUtcNow();
        if (heartbeat.LastSuccessAt is { } success && now - success < FreshWindow)
            return HealthOutcome.Ok("Connected");

        if (heartbeat.LastFailure is { } failure)
        {
            return failure.Failure switch
            {
                PollFailure.Unauthorized => HealthOutcome.Failing("Telegram rejected the bot token"),
                PollFailure.Network => HealthOutcome.Warning("Offline — this is normal"),
                _ => HealthOutcome.Failing("Telegram poll failed"),
            };
        }

        return heartbeat.LastSuccessAt is not null
            ? HealthOutcome.Warning("Stale — no successful poll recently")
            : HealthOutcome.Warning("Waiting for the first poll");
    }
}
