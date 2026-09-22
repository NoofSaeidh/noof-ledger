using Telegram.Bot.Types;

namespace Noof.Ledger.Telegram;

internal interface ITelegramUpdateRouter
{
    Task HandleAsync(Update update, string timeZoneId, CancellationToken cancellationToken);
}
