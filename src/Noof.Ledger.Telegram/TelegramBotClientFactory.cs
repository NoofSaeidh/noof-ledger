using Telegram.Bot;

namespace Noof.Ledger.Telegram;

public sealed class TelegramBotClientFactory(IHttpClientFactory httpClientFactory) : ITelegramBotClientFactory
{
    public ITelegramBotClient Create(string token) =>
        new TelegramBotClient(token, httpClientFactory.CreateClient("telegram"));
}
