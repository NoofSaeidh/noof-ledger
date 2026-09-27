using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Receipts;
using Telegram.Bot;

namespace Noof.Ledger.Telegram;

internal sealed class TelegramReceiptPhotoSource(TelegramClientHandle clientHandle) : IReceiptPhotoSource
{
    // Mirrors TelegramVoiceFileSource's reasoning: refuses before spending a download on something
    // no reasonable receipt photo would ever be. Telegram's own file API caps everything at 20 MB.
    // Enforced twice against the one constant: FileSize is Telegram's own metadata and the cheap
    // first gate, but it can be missing, stale or simply wrong, so the copy below counts the bytes
    // actually received and stops as soon as they pass the same limit (Copilot finding, PR #3) -
    // it never buffers past the limit, so a lying FileSize cannot be used to force an unbounded
    // MemoryStream.
    const long MaxBytes = 10 * 1024 * 1024;

    public async Task<ReceiptPhoto> DownloadAsync(string fileId, CancellationToken cancellationToken)
    {
        var client = clientHandle.Current ?? throw new InvalidOperationException(
            "The Telegram client is not ready yet: no bot token has been saved.");

        var file = await client.GetFile(fileId, cancellationToken);
        if (file.FileSize is { } size && size > MaxBytes)
            throw OverLimit(fileId, size);

        var buffer = new SizeLimitedBuffer(MaxBytes);
        try
        {
            await client.DownloadFile(file, buffer, cancellationToken);
        }
        catch (StreamSizeLimitExceededException ex)
        {
            // Unlike the metadata pre-check above (cheap to retry: it never starts a download), this
            // is Terminal rather than Transient - the worker's generic catch would otherwise retry a
            // download that has already proven itself oversized, re-downloading the same bytes on
            // every attempt up to the attempt cap for no different outcome.
            throw new ModelCallException(
                ModelFailureKind.Terminal, OverLimitMessage(fileId, ex.BytesReceived));
        }

        return new ReceiptPhoto(buffer.ToArray(), MediaTypeFor(file.FilePath));
    }

    static InvalidOperationException OverLimit(string fileId, long size) =>
        new(OverLimitMessage(fileId, size));

    static string OverLimitMessage(string fileId, long size) =>
        $"Receipt photo {fileId} is {size} bytes, over the {MaxBytes}-byte limit.";

    static string MediaTypeFor(string? filePath) => Path.GetExtension(filePath)?.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/jpeg",
    };
}
