namespace Noof.Ledger.Ai.Groq;

internal sealed class GroqOptions
{
    public string Model { get; init; } = "whisper-large-v3";

    // A missing trailing slash makes Uri's own relative-resolution rules drop the last path segment
    // (e.g. "v1") when combined with a relative path such as "audio/transcriptions" - normalising here
    // means every caller of BaseAddress, bound from configuration or not, gets the trailing slash.
    public Uri BaseAddress
    {
        get;
        init => field = value.AbsoluteUri.EndsWith('/') ? value : new Uri(value.AbsoluteUri + "/");
    } = new("https://api.groq.com/openai/v1/");

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
}
