namespace Noof.Ledger.Application.Capture;

// Exactly one of TelegramFileId (a photo or image document) or VerificationUrl (a fiscal QR link
// sent as plain text) is set - ExtractReceiptWorker decides which by whichever is present. The
// router never lets both through: a photo's caption is carried as Caption, never scanned for a
// fiscal link, so today it cannot produce both sources (TelegramUpdateRouterTests documents this).
public sealed record CapturedReceipt(
    long ChatId, int MessageId, DateTimeOffset SentAt, string? Caption, string? TelegramFileId, string? VerificationUrl)
{
    // Same reason as CapturedVoice.SentAt: fail here, where it is built, not deep inside
    // SaveChangesAsync with an Npgsql message that names neither this type nor the property.
    public DateTimeOffset SentAt { get; init; } = SentAt.Offset == TimeSpan.Zero
        ? SentAt
        : throw new ArgumentException($"must be UTC (Offset == TimeSpan.Zero), but was {SentAt.Offset}.", nameof(SentAt));

    public string? TelegramFileId { get; init; } = (TelegramFileId is not null) != (VerificationUrl is not null)
        ? TelegramFileId
        : throw new ArgumentException(
            $"Exactly one of {nameof(TelegramFileId)} or {nameof(VerificationUrl)} must be set.", nameof(TelegramFileId));
}
