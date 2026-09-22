using Telegram.Bot;

namespace Noof.Ledger.Telegram;

internal interface ITelegramBotClientFactory
{
    ITelegramBotClient Create(string token);
}
