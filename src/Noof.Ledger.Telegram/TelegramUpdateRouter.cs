using Noof.Ledger.Application.Capture;
using Noof.Ledger.Application.Chat;
using Telegram.Bot.Types;

namespace Noof.Ledger.Telegram;

public sealed class TelegramUpdateRouter(
    ICaptureStore captureStore,
    IChatNotifier chatNotifier,
    TelegramOwnerGate ownerGate,
    TimeProvider timeProvider)
    : ITelegramUpdateRouter
{
    public const string ReceiptAcknowledgement = "Saved. I'll add the amount once it's categorised.";

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

        var captured = new CapturedMessage(message.Chat.Id, message.Id, text, timeProvider.GetUtcNow());
        var transactionId = await captureStore.CaptureAsync(captured, timeZoneId, cancellationToken);

        var botMessageId = await chatNotifier.SendAsync(message.Chat.Id, ReceiptAcknowledgement, cancellationToken);
        await captureStore.AttachBotMessageAsync(transactionId, botMessageId, cancellationToken);
    }
}
