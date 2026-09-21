using Telegram.Bot;

namespace Noof.Ledger.Telegram;

public sealed class TelegramClientHandle
{
    public ITelegramBotClient? Current { get; set; }
}
