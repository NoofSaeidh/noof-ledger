using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Transcription;

namespace Noof.Ledger.Ai.Diagnostics;

// Reads IModelProvider/ISpeechProvider so this check needs no knowledge of which provider answers.
internal sealed class AiKeysHealthCheck(IDatabaseGate gate, IModelProvider modelProvider, ISpeechProvider speechProvider) : ISystemHealthCheck
{
    public string Name => "AI keys";

    public int Order => 40;

    public string LogCategory => "Noof.Ledger.Ai";

    public async Task<HealthOutcome> CheckAsync(CancellationToken cancellationToken)
    {
        if (gate.State is not DatabaseState.Ready)
            return HealthOutcome.Warning("Waiting for the database");

        var modelConfigured = await modelProvider.IsConfiguredAsync(cancellationToken);
        var speechConfigured = await speechProvider.IsConfiguredAsync(cancellationToken);

        if (modelConfigured && speechConfigured)
            return HealthOutcome.Ok("Configured");

        List<string> missing = [];
        if (!modelConfigured)
            missing.Add(modelProvider.SecretLabel);
        if (!speechConfigured)
            missing.Add(speechProvider.SecretLabel);

        return HealthOutcome.Failing($"Missing: {string.Join(", ", missing)}");
    }
}
