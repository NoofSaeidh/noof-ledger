namespace Noof.Ledger.Application.Receipts;

// Downscales a receipt photo before it ever reaches the vision fallback (2026-09-27): Telegram's own
// compression rarely exceeds ChatReceiptVision's 5 MB refusal, but a phone's full-resolution photo, or
// a photo sent as an uncompressed file, both can. IQrReader keeps reading the original bytes -
// downscaling is the vision path's own concern, never the QR decoder's.
public interface IReceiptImageScaler
{
    ReceiptPhoto ScaleForVision(ReceiptPhoto photo);
}
