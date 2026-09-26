namespace Noof.Ledger.Demo;

internal static class MockData
{
    public const string TimeZoneId = "Europe/Belgrade";
    public const string Username = "demo";
    public const string Password = "demo";
    public const long TelegramChatId = 555_000_001;
    public const string FakeKey = "demo-not-a-real-key";
    public const string AnthropicKeySecret = "anthropic-api-key";
    public const string GroqKeySecret = "groq-api-key";

    public static readonly DateTimeOffset Now = new(2026, 9, 20, 18, 0, 0, TimeSpan.Zero);
    public static readonly Guid UserId = Guid.Parse("7a1c0000-0000-4000-8000-0000000000aa");
}
