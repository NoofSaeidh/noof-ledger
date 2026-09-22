using System.Globalization;
using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Telegram;

internal sealed class TelegramUpdateOffsetStore(ISecretStore secretStore)
{
    public const string Key = "telegram-update-offset";

    public async Task<int?> GetAsync(CancellationToken cancellationToken)
    {
        var result = await secretStore.GetAsync(Key, cancellationToken);

        return result.State is SecretState.Present && int.TryParse(result.Value, out var offset)
            ? offset
            : null;
    }

    public Task SetAsync(int offset, CancellationToken cancellationToken) =>
        secretStore.SetAsync(Key, offset.ToString(CultureInfo.InvariantCulture), cancellationToken);
}
