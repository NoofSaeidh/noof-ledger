using Microsoft.Extensions.Diagnostics.HealthChecks;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Telegram.Diagnostics;

internal sealed class TelegramHealthCheck(
    IDatabaseGate gate, ISecretStore secretStore, IPollingHeartbeat heartbeat, TimeProvider timeProvider) : IHealthCheck
{
    static readonly TimeSpan FreshWindow = TimeSpan.FromMinutes(2);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (gate.State is not DatabaseState.Ready)
            return HealthCheckResult.Degraded("Waiting for the database");

        var token = await secretStore.GetStatusAsync(SecretKeys.TelegramBotToken, cancellationToken);
        if (token.State is not SecretState.Present)
            return HealthCheckResult.Unhealthy("No Telegram bot token configured");

        var now = timeProvider.GetUtcNow();
        if (heartbeat.LastSuccessAt is { } success && now - success < FreshWindow)
            return HealthCheckResult.Healthy("Connected");

        if (heartbeat.LastFailure is { } failure)
        {
            return failure.Failure switch
            {
                PollFailure.Unauthorized => HealthCheckResult.Unhealthy("Telegram rejected the bot token"),
                PollFailure.Network => HealthCheckResult.Degraded("Offline — this is normal"),
                _ => HealthCheckResult.Unhealthy("Telegram poll failed"),
            };
        }

        return heartbeat.LastSuccessAt is not null
            ? HealthCheckResult.Degraded("Stale — no successful poll recently")
            : HealthCheckResult.Degraded("Waiting for the first poll");
    }
}
