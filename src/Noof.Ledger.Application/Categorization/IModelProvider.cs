using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Application.Categorization;

// The model provider as the host and the settings page may see it, without either of them naming
// it: which stored secret configures it (ISecretProbe.SecretKey), what to call that secret on
// screen, whether it is set, and - through ISecretProbe - whether it actually works.
public interface IModelProvider : ISecretProbe
{
    string SecretLabel { get; }

    Task<bool> IsConfiguredAsync(CancellationToken cancellationToken);
}
