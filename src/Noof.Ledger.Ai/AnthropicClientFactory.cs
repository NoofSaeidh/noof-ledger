using Anthropic;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Ai;

public interface IAnthropicClientFactory
{
    // Never caches; always returns the raw client. Callers that want an IChatClient call
    // raw.AsIChatClient(options.Model) themselves — AnthropicKeyProbe needs the raw client
    // (Models.List), so the factory cannot commit to one abstraction for every caller.
    Task<AnthropicClient> CreateAsync(CancellationToken cancellationToken);
}

public sealed class AnthropicClientFactory(
    ISecretStore secretStore, HttpClient httpClient, AnthropicOptions options) : IAnthropicClientFactory
{
    public async Task<AnthropicClient> CreateAsync(CancellationToken cancellationToken)
    {
        var secret = await secretStore.GetAsync(SecretKeys.AnthropicApiKey, cancellationToken);

        return secret.State switch
        {
            SecretState.Present => new AnthropicClient
            {
                ApiKey = secret.Value!,
                HttpClient = httpClient,
                MaxRetries = 0,
                Timeout = options.Timeout,
            },
            SecretState.Missing => throw new ModelCallException(
                ModelFailureKind.Terminal,
                "No Anthropic API key is configured. The worker is expected to check this before claiming work; " +
                "reaching this path means that check was skipped or the key was cleared mid-job."),
            SecretState.Unreadable => throw new ModelCallException(
                ModelFailureKind.Terminal,
                "The stored Anthropic API key could not be decrypted."),
            _ => throw new InvalidOperationException($"Unhandled {nameof(SecretState)} value: {secret.State}."),
        };
    }
}
