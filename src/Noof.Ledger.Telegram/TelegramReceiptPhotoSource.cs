using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Receipts;
using Telegram.Bot;

namespace Noof.Ledger.Telegram;

internal sealed class TelegramReceiptPhotoSource(TelegramClientHandle clientHandle) : IReceiptPhotoSource
{
    // Refuses before spending a download on something no reasonable receipt photo would ever be.
    // Telegram's own file API caps everything at 20 MB. Enforced twice against the one constant:
    // FileSize is Telegram's own metadata and the cheap first gate, but it can be missing, stale or
    // simply wrong, so the copy below counts the bytes actually received and stops as soon as they
    // pass the same limit (Copilot finding, PR #3) - it never buffers past the limit, so a lying
    // FileSize cannot be used to force an unbounded MemoryStream. Both checks map to the same
    // ModelCallException(Terminal, ...) - retrying either would only re-download the same oversized
    // file for no different outcome (TelegramVoiceFileSource has no such check: it downloads
    // unbounded into MemoryStream today - docs/backlog/deferred-from-phase-6-receipts.md).
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
        catch (Exception ex) when (FindSizeLimitExceeded(ex) is { } sizeLimitExceeded)
        {
            // Telegram.Bot's own DownloadFile wraps every exception CopyToAsync throws - including
            // this one - in its own RequestException(statusCode, innerException), so the exception
            // that actually reaches here is never StreamSizeLimitExceededException itself; walking
            // InnerException is the only way to recognise it (verified by decompiling Telegram.Bot
            // 22.10.3.1, not from its docs).
            throw OverLimit(fileId, sizeLimitExceeded.BytesReceived);
        }

        return new ReceiptPhoto(buffer.ToArray(), MediaTypeFor(file.FilePath));
    }

    static StreamSizeLimitExceededException? FindSizeLimitExceeded(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is StreamSizeLimitExceededException sizeLimitExceeded)
                return sizeLimitExceeded;
        }

        return null;
    }

    static ModelCallException OverLimit(string fileId, long size) =>
        new(ModelFailureKind.Terminal, OverLimitMessage(fileId, size));

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
