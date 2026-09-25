using Microsoft.Extensions.Diagnostics.HealthChecks;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Transcription;

namespace Noof.Ledger.Host.Diagnostics;

// Reads IModelProvider/ISpeechProvider - never the AI assembly or a raw ISecretStore key - so this
// check needs no knowledge of which provider answers, matching HealthCheckBoundaryTests and the
// pre-existing AiBoundaryTests rule that only that assembly may name the provider.
internal sealed class AiKeysHealthCheck(IDatabaseGate gate, IModelProvider modelProvider, ISpeechProvider speechProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (gate.State is not DatabaseState.Ready)
            return HealthCheckResult.Degraded("Waiting for the database");

        var modelConfigured = await modelProvider.IsConfiguredAsync(cancellationToken);
        var speechConfigured = await speechProvider.IsConfiguredAsync(cancellationToken);

        if (modelConfigured && speechConfigured)
            return HealthCheckResult.Healthy("Configured");

        List<string> missing = [];
        if (!modelConfigured)
            missing.Add(modelProvider.SecretLabel);
        if (!speechConfigured)
            missing.Add(speechProvider.SecretLabel);

        return HealthCheckResult.Unhealthy($"Missing: {string.Join(", ", missing)}");
    }
}
