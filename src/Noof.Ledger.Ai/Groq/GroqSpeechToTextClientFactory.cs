using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.AI;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Application.Transcription;

namespace Noof.Ledger.Ai.Groq;

internal sealed class GroqSpeechToTextClientFactory(ISecretStore secretStore, HttpClient httpClient, GroqOptions options)
    : ISpeechToTextClientFactory, ISpeechProvider
{
    // Data Protection's purpose is derived from this string: renaming it orphans the operator's stored key.
    const string ApiKeySecret = "groq-api-key";

    public string SecretKey => ApiKeySecret;

    public string SecretLabel => "Groq API key";

    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken) =>
        (await secretStore.GetStatusAsync(ApiKeySecret, cancellationToken)).State is SecretState.Present;

    public async Task<ISpeechToTextClient> CreateAsync(CancellationToken cancellationToken) =>
        new GroqSpeechToTextClient(httpClient, await ReadKeyAsync(cancellationToken), options);

    // GET /models costs nothing and proves the key without transcribing anything.
    public async Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var apiKey = await ReadKeyAsync(cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(options.BaseAddress, "models"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await httpClient.SendAsync(request, cancellationToken);

            return response.StatusCode switch
            {
                HttpStatusCode.OK => new ProbeResult(true, "Groq accepted the key."),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new ProbeResult(false, "Groq rejected the key."),
                var status => new ProbeResult(false, $"Groq answered with status {(int)status}."),
            };
        }
        catch (ModelCallException ex)
        {
            return new ProbeResult(false, ex.Message);
        }
        catch (HttpRequestException)
        {
            return new ProbeResult(false, "Could not reach Groq.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProbeResult(false, "Groq did not answer in time.");
        }
        // Never ex.Message: an unexpected exception could carry the key.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProbeResult(false, "The key could not be tested.");
        }
    }

    async Task<string> ReadKeyAsync(CancellationToken cancellationToken)
    {
        var secret = await secretStore.GetAsync(ApiKeySecret, cancellationToken);
        return secret.State switch
        {
            // Present always carries its value.
            SecretState.Present => secret.Value!,
            SecretState.Missing => throw new ModelCallException(ModelFailureKind.Terminal,
                "No Groq API key is configured. Enter one on the settings page."),
            SecretState.Unreadable => throw new ModelCallException(ModelFailureKind.Terminal,
                "The stored Groq API key could not be decrypted."),
            _ => throw new InvalidOperationException($"Unknown secret state {secret.State}."),
        };
    }
}
