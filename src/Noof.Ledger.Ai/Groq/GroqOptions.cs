namespace Noof.Ledger.Ai.Groq;

internal sealed class GroqOptions
{
    public string Model { get; init; } = "whisper-large-v3";

    public Uri BaseAddress { get; init; } = new("https://api.groq.com/openai/v1/");

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
}
