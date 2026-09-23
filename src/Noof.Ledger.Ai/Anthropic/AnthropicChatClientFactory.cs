using Anthropic;
using Anthropic.Exceptions;
using Microsoft.Extensions.AI;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Ai.Anthropic;

// Everything that makes the model provider Anthropic: its stored key, its client, how it is told
// to call tools strictly, what its failures mean (AnthropicTranslatingChatClient), and how the
// settings page tests its key. Everything above this reaches it through IChatClientFactory or
// IModelProvider and never learns its name.
internal sealed class AnthropicChatClientFactory(ISecretStore secretStore, HttpClient httpClient, AnthropicOptions options)
    : IChatClientFactory, IModelProvider
{
    // The operator's real key is stored encrypted under exactly this string, and EfSecretStore
    // builds the Data Protection purpose from it: changing one character orphans that key.
    const string ApiKeySecret = "anthropic-api-key";

    public string SecretKey => ApiKeySecret;

    public string SecretLabel => "Anthropic API key";

    // Status, not the plaintext: nothing is decrypted into memory just to learn that a key exists,
    // and a lost key ring still reads as not configured (Unreadable) rather than as present.
    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken) =>
        (await secretStore.GetStatusAsync(ApiKeySecret, cancellationToken)).State is SecretState.Present;

    public async Task<IChatClient> CreateAsync(CancellationToken cancellationToken)
    {
        var client = await CreateSdkClientAsync(cancellationToken);
        return new AnthropicTranslatingChatClient(client.AsIChatClient(options.Model, options.MaxTokens));
    }

    // GET /v1/models costs no tokens - the only network call the settings page's Test button may
    // make. Exception-total by contract: the button's whole job is telling the operator whether the
    // key works, never throwing into the Blazor circuit.
    public async Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var client = await CreateSdkClientAsync(cancellationToken);
            // List(cancellationToken) does NOT compile - the first parameter is ModelListParams?.
            var page = await client.Models.List(null, cancellationToken);
            return new ProbeResult(true, $"Connected. {page.Items.Count} model(s) visible to this key.");
        }
        catch (ModelCallException ex)
        {
            return new ProbeResult(false, ex.Message);
        }
        catch (AnthropicApiException ex)
        {
            return new ProbeResult(false, $"Anthropic returned status {(int)ex.StatusCode}.");
        }
        catch (Exception ex) when (ex is AnthropicIOException or HttpRequestException)
        {
            return new ProbeResult(false, "Could not reach Anthropic: a network error occurred.");
        }
        // A fixed string, not ex.Message: an unmodelled failure could originate anywhere in the SDK
        // or transport, and nothing proves in general that its message never echoes request state
        // such as the key itself.
        catch (Exception)
        {
            return new ProbeResult(false, "The connection test failed unexpectedly.");
        }
    }

    async Task<AnthropicClient> CreateSdkClientAsync(CancellationToken cancellationToken)
    {
        var secret = await secretStore.GetAsync(ApiKeySecret, cancellationToken);

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
