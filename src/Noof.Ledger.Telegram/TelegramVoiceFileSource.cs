using Noof.Ledger.Application.Transcription;
using Telegram.Bot;

namespace Noof.Ledger.Telegram;

internal sealed class TelegramVoiceFileSource(TelegramClientHandle clientHandle) : IVoiceFileSource
{
    public async Task<Stream> DownloadAsync(string voiceFileId, CancellationToken cancellationToken)
    {
        var client = clientHandle.Current ?? throw new InvalidOperationException(
            "The Telegram client is not ready yet: no bot token has been saved.");

        var audio = new MemoryStream();
        await client.GetInfoAndDownloadFile(voiceFileId, audio, cancellationToken);
        audio.Position = 0;
        return audio;
    }
}
