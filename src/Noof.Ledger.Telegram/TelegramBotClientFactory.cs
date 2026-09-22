using Telegram.Bot;

namespace Noof.Ledger.Telegram;

internal sealed class TelegramBotClientFactory(IHttpClientFactory httpClientFactory) : ITelegramBotClientFactory
{
    public ITelegramBotClient Create(string token) =>
        new TelegramBotClient(token, httpClientFactory.CreateClient("telegram"));
}
