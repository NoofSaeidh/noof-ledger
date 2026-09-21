using System.Globalization;
using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Telegram;

public sealed class TelegramOwnerGate(ISecretStore secretStore)
{
    public async Task<bool> IsAllowedAsync(long chatId, CancellationToken cancellationToken)
    {
        var owner = await secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, cancellationToken);

        return owner.State switch
        {
            SecretState.Missing => await ClaimAsync(chatId, cancellationToken),
            SecretState.Present => owner.Value == chatId.ToString(CultureInfo.InvariantCulture),
            _ => false,
        };
    }

    async Task<bool> ClaimAsync(long chatId, CancellationToken cancellationToken)
    {
        await secretStore.SetAsync(SecretKeys.TelegramOwnerChatId, chatId.ToString(CultureInfo.InvariantCulture), cancellationToken);
        return true;
    }
}
