using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Application.Transcription;

// IModelProvider's counterpart for speech: whether its key is stored, what the settings page calls it, and - through
// ISecretProbe - whether the key works. The provider's name never leaves Noof.Ledger.Ai.
public interface ISpeechProvider : ISecretProbe
{
    string SecretLabel { get; }

    Task<bool> IsConfiguredAsync(CancellationToken cancellationToken);
}
