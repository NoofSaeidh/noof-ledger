using Noof.Ledger.Application.Receipts;
using Telegram.Bot;

namespace Noof.Ledger.Telegram;

internal sealed class TelegramReceiptPhotoSource(TelegramClientHandle clientHandle) : IReceiptPhotoSource
{
    // Mirrors TelegramVoiceFileSource's reasoning: refuses before spending a download on something
    // no reasonable receipt photo would ever be. Telegram's own file API caps everything at 20 MB.
    const long MaxBytes = 10 * 1024 * 1024;

    public async Task<ReceiptPhoto> DownloadAsync(string fileId, CancellationToken cancellationToken)
    {
        var client = clientHandle.Current ?? throw new InvalidOperationException(
            "The Telegram client is not ready yet: no bot token has been saved.");

        var file = await client.GetFile(fileId, cancellationToken);
        if (file.FileSize is { } size && size > MaxBytes)
        {
            throw new InvalidOperationException(
                $"Receipt photo {fileId} is {size} bytes, over the {MaxBytes}-byte limit.");
        }

        var buffer = new MemoryStream();
        await client.DownloadFile(file, buffer, cancellationToken);
        return new ReceiptPhoto(buffer.ToArray(), MediaTypeFor(file.FilePath));
    }

    static string MediaTypeFor(string? filePath) => Path.GetExtension(filePath) switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/jpeg",
    };
}
