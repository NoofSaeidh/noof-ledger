using Telegram.Bot;

namespace Noof.Ledger.Telegram;

public interface ITelegramBotClientFactory
{
    ITelegramBotClient Create(string token);
}
