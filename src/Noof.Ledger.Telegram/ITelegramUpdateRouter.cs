using Telegram.Bot.Types;

namespace Noof.Ledger.Telegram;

public interface ITelegramUpdateRouter
{
    Task HandleAsync(Update update, string timeZoneId, CancellationToken cancellationToken);
}
