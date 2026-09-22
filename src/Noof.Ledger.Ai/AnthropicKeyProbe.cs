using Anthropic.Exceptions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Ai;

// GET /v1/models costs no tokens — this is the only network call a "Test" button in the settings
// page (Task 8) is allowed to trigger. Uses the raw AnthropicClient directly, never IChatClient:
// listing models has nothing to do with chat, and the SDK's own Models.List is the only surface
// for it either way.
internal sealed class AnthropicKeyProbe(IAnthropicClientFactory clientFactory) : ISecretProbe
{
    public string SecretKey => SecretKeys.AnthropicApiKey;

    public async Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var client = await clientFactory.CreateAsync(cancellationToken);
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
        catch (HttpRequestException)
        {
            return new ProbeResult(false, "Could not reach Anthropic: a network error occurred.");
        }
        // Exception-total by contract: the Test button's whole job is telling the operator whether
        // this key works, never throwing into the Blazor circuit - a request timeout surfaces as
        // TaskCanceledException, and the client factory can throw InvalidOperationException, neither
        // caught above. The message here is a fixed string, not ex.Message: an unmodeled failure
        // could originate anywhere in the SDK or transport and there is no way to prove in general
        // that its message never echoes back request state such as the key itself.
        catch (Exception)
        {
            return new ProbeResult(false, "The connection test failed unexpectedly.");
        }
    }
}
