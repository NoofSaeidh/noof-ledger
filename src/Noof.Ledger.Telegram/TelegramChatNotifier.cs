using Noof.Ledger.Application.Chat;
using Telegram.Bot;

namespace Noof.Ledger.Telegram;

internal sealed class TelegramChatNotifier(TelegramClientHandle clientHandle) : IChatNotifier
{
    public async Task<int> SendAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        var message = await Client().SendMessage(chatId, text, cancellationToken: cancellationToken);
        return message.Id;
    }

    public async Task EditAsync(long chatId, int messageId, string text, CancellationToken cancellationToken) =>
        await Client().EditMessageText(chatId, messageId, text, cancellationToken: cancellationToken);

    ITelegramBotClient Client() =>
        clientHandle.Current ?? throw new InvalidOperationException(
            "The Telegram client is not ready yet: no bot token has been saved.");
}
