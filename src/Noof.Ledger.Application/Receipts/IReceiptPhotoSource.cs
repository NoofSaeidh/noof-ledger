namespace Noof.Ledger.Application.Receipts;

public sealed record ReceiptPhoto(ReadOnlyMemory<byte> Bytes, string MediaType);

public interface IReceiptPhotoSource
{
    // The photo (or image document) behind a receipt capture, read into memory - the caller disposes
    // nothing, there is no stream. A failed download propagates: ExtractReceiptWorker treats most
    // failures as transient, same as IVoiceFileSource.DownloadAsync, except a size-limit rejection
    // (ModelCallException, ModelFailureKind.Terminal) - retrying that would only re-download the same
    // oversized file.
    Task<ReceiptPhoto> DownloadAsync(string fileId, CancellationToken cancellationToken);
}
