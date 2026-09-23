using Noof.Ledger.Application.Capture;
using Noof.Ledger.Application.Chat;
using Telegram.Bot.Types;

namespace Noof.Ledger.Telegram;

internal sealed class TelegramUpdateRouter(
    ICaptureStore captureStore,
    IChatNotifier chatNotifier,
    TelegramOwnerGate ownerGate)
    : ITelegramUpdateRouter
{
    public async Task HandleAsync(Update update, string timeZoneId, CancellationToken cancellationToken)
    {
        var message = update.Message;
        if (message is null)
            return;

        // Reject before reading Text: a stranger's content must never be inspected, not even to
        // decide whether it looks like a spend.
        if (!await ownerGate.IsAllowedAsync(message.Chat.Id, cancellationToken))
            return;

        if (message.Text is not { Length: > 0 } text)
            return;

        // message.Date is when Telegram received it from the sender, not when we got around to
        // processing it -- an outage can queue a message for hours, and every queued message must
        // keep its own moment so time_zone_id buckets it into the correct local day later.
        // Message.Date deserialises as DateTime with Kind=Utc (confirmed against Telegram.Bot
        // 22.10.3.1's UnixDateTimeConverter), so this offset is genuinely zero, not just labelled so.
        var sentAt = new DateTimeOffset(message.Date);
        var captured = new CapturedMessage(message.Chat.Id, message.Id, text, sentAt);
        var transactionId = await captureStore.CaptureAsync(captured, timeZoneId, cancellationToken);

        var botMessageId = await chatNotifier.SendAsync(message.Chat.Id, RecordEcho.Acknowledgement, cancellationToken);
        await captureStore.AttachBotMessageAsync(transactionId, botMessageId, cancellationToken);
    }
}
