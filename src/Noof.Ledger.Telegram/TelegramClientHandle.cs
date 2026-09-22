using Telegram.Bot;

namespace Noof.Ledger.Telegram;

internal sealed class TelegramClientHandle
{
    public ITelegramBotClient? Current { get; set; }
}
