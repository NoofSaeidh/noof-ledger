namespace Noof.Ledger.Ai.Tests.Groq;

static class LiveTranscriptionGate
{
    public const string KeyVariable = "NOOF_LEDGER_LIVE_GROQ_KEY";
    public const string FileVariable = "NOOF_LEDGER_LIVE_VOICE_FILE";

    public static string SkipMessage { get; } =
        $"No live Groq run - set both {KeyVariable} and {FileVariable} (a voice note kept outside the repository) to run it " +
        "(see tests/Noof.Ledger.Ai.Tests/Groq/LiveTranscriptionTests.cs).";

    public static bool TryGet(out string apiKey, out string voiceFile) =>
        TryParse(Environment.GetEnvironmentVariable(KeyVariable), Environment.GetEnvironmentVariable(FileVariable),
            out apiKey, out voiceFile);

    internal static bool TryParse(string? rawKey, string? rawFile, out string apiKey, out string voiceFile)
    {
        apiKey = rawKey ?? "";
        voiceFile = rawFile ?? "";
        return !string.IsNullOrWhiteSpace(rawKey) && !string.IsNullOrWhiteSpace(rawFile);
    }
}
