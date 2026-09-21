namespace Noof.Ledger.Ai.Tests;

// The ONLY place in the repository that reads NOOF_LEDGER_LIVE_ANTHROPIC_KEY. This is a
// test-only variable: nothing under src/ ever reads it, the application's own Anthropic key
// lives encrypted in app_secret and is entered through /settings/secrets, and this rule does not
// bend for tests - a test process just has no database of its own to read that key from. The
// name is deliberately unlike any application configuration key so it can never be mistaken for
// one, and it must never be written to a committed file.
static class LiveModelGate
{
    public const string EnvironmentVariableName = "NOOF_LEDGER_LIVE_ANTHROPIC_KEY";

    public static string SkipMessage { get; } =
        $"No live Anthropic key - set {EnvironmentVariableName} to run this suite " +
        "(see tests/Noof.Ledger.Ai.Tests/LiveModelTests.cs).";

    public static bool TryGetApiKey(out string apiKey) =>
        TryParse(Environment.GetEnvironmentVariable(EnvironmentVariableName), out apiKey);

    internal static bool TryParse(string? rawValue, out string apiKey)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            apiKey = "";
            return false;
        }

        apiKey = rawValue;
        return true;
    }
}
