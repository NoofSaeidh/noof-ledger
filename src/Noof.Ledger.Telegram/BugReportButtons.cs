using System.Globalization;
using Telegram.Bot.Types.ReplyMarkups;

namespace Noof.Ledger.Telegram;

// The bug reports' own button vocabulary. Application names none of it, so a reply with a button needs no Telegram
// type outside this assembly; the "bug:" prefix keeps it apart from RecordActionButtons' data.
internal static class BugReportButtons
{
    public const string CloseLabel = "Close report";
    public const string DataPrefix = "bug:";
    const string ClosePrefix = "bug:close:";

    public static InlineKeyboardButton Close(int number) =>
        InlineKeyboardButton.WithCallbackData(CloseLabel, string.Create(CultureInfo.InvariantCulture, $"{ClosePrefix}{number}"));

    public static bool IsBugReportData(string? data) =>
        data is not null && data.StartsWith(DataPrefix, StringComparison.Ordinal);

    public static bool TryParseClose(string? data, out int number)
    {
        number = 0;
        if (data is null || !data.StartsWith(ClosePrefix, StringComparison.Ordinal))
            return false;

        // NumberStyles.None: ASCII digits only — no sign, no space, no other script's digits.
        if (!int.TryParse(data.AsSpan(ClosePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0)
            return false;

        number = parsed;
        return true;
    }
}
