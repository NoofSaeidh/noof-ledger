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
        var chatIdText = chatId.ToString(CultureInfo.InvariantCulture);

        if (await secretStore.TrySetIfMissingAsync(SecretKeys.TelegramOwnerChatId, chatIdText, cancellationToken))
            return true;

        // Lost the race: another chat claimed ownership between our GetAsync and this call. Honour
        // whoever actually won rather than surfacing the conflict as an error - the loser here is
        // simply not the owner, which is the correct outcome, not a failure.
        var owner = await secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, cancellationToken);
        return owner.State is SecretState.Present && owner.Value == chatIdText;
    }
}
