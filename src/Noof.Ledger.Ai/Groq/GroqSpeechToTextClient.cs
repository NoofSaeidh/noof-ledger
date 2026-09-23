using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Noof.Ledger.Application.Categorization;

namespace Noof.Ledger.Ai.Groq;

// Groq ships no .NET SDK, and owning the request lets the tests assert exactly what goes on the wire (V2). It never
// retries: retrying is the job queue's.
internal sealed class GroqSpeechToTextClient(HttpClient httpClient, string apiKey, GroqOptions groqOptions) : ISpeechToTextClient
{
    // Telegram names a voice note .oga, and Groq refuses that name with 400 while accepting the same bytes as .ogg.
    const string FileName = "voice.ogg";

    public async Task<SpeechToTextResponse> GetTextAsync(
        Stream audioSpeechStream, SpeechToTextOptions? options = null, CancellationToken cancellationToken = default)
    {
        var model = options?.ModelId ?? groqOptions.Model;

        using var content = new MultipartFormDataContent();
        var audio = new StreamContent(audioSpeechStream);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/ogg");
        content.Add(audio, "file", FileName);
        content.Add(new StringContent(model), "model");
        if (options?.SpeechLanguage is { } language)
            content.Add(new StringContent(language), "language");
        content.Add(new StringContent("json"), "response_format");

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(groqOptions.BaseAddress, "audio/transcriptions"))
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        return new SpeechToTextResponse(ReadText(body)) { ModelId = model };
    }

    async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new ModelCallException(ModelFailureKind.Transient, "Groq could not be reached.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelCallException(ModelFailureKind.Transient, "Groq did not answer in time.", ex);
        }

        if (response.IsSuccessStatusCode)
            return response;

        // The body is not read into the message: an error body may quote the request back, key included.
        var status = response.StatusCode;
        response.Dispose();
        var exception = new ModelCallException(Classify(status), $"Groq transcription failed with status {(int)status}.");
        throw IsAccountLevel(status) ? exception.AsAccountLevel() : exception;
    }

    static string ReadText(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.GetProperty("text").GetString() ?? string.Empty;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new ModelCallException(ModelFailureKind.Terminal, "Groq answered with no transcript in it.", ex);
        }
    }

    // What the request itself got wrong is terminal. A rate limit (the free tier allows 20 requests a minute), a
    // server error and anything unrecognised are worth another attempt.
    static ModelFailureKind Classify(HttpStatusCode status) => status switch
    {
        HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.PaymentRequired
            or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.RequestEntityTooLarge
            or HttpStatusCode.UnsupportedMediaType or HttpStatusCode.UnprocessableEntity => ModelFailureKind.Terminal,
        _ => ModelFailureKind.Transient,
    };

    static bool IsAccountLevel(HttpStatusCode status) =>
        status is HttpStatusCode.Unauthorized or HttpStatusCode.PaymentRequired or HttpStatusCode.Forbidden;

    public IAsyncEnumerable<SpeechToTextResponseUpdate> GetStreamingTextAsync(
        Stream audioSpeechStream, SpeechToTextOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A voice note is transcribed whole; nothing here streams.");

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    // The HttpClient belongs to IHttpClientFactory, not to this client.
    public void Dispose()
    {
    }
}
