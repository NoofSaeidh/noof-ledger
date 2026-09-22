namespace Noof.Ledger.Telegram;

internal static class TelegramBackoff
{
    static readonly TimeSpan Cap = TimeSpan.FromMinutes(1);

    public static TimeSpan Compute(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
            return TimeSpan.Zero;

        var seconds = Math.Min(Cap.TotalSeconds, Math.Pow(2, consecutiveFailures));
        return TimeSpan.FromSeconds(seconds);
    }
}
