using System.Globalization;

namespace Noof.Ledger.Telegram;

// A Telegram bug report's ReplyTo and its delivered reply's reference, both opaque outside this assembly (R-2). The
// address is written exactly as the BugReportsReplyTo migration back-filled the reports filed before it, or their
// redelivery would no longer find them.
internal readonly record struct TelegramReplyAddress(long ChatId, int MessageId)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{ChatId}:{MessageId}");

    // Only what ToString writes: a group chat's id may carry a minus, nothing else a sign, a space or a leading zero.
    // The refusal names neither id - a Telegram id never reaches a log line.
    public static TelegramReplyAddress Parse(string text)
    {
        var separator = text.IndexOf(':', StringComparison.Ordinal);
        if (separator > 0
            && long.TryParse(text.AsSpan(0, separator), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var chatId)
            && int.TryParse(text.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var messageId)
            && new TelegramReplyAddress(chatId, messageId) is var address
            && address.ToString() == text)
            return address;

        throw new FormatException("A Telegram reply address is \"<chat id>:<message id>\" in invariant digits.");
    }

    public static string DeliveredAs(int replyMessageId) => replyMessageId.ToString(CultureInfo.InvariantCulture);
}
