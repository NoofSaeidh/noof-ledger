namespace Noof.Ledger.Application.Capture;

public sealed record CapturedVoice(long ChatId, int MessageId, string VoiceFileId, int DurationSeconds, DateTimeOffset SentAt)
{
    // For CapturedMessage.SentAt's reason: a non-UTC instant fails here, where it is built, not deep inside
    // SaveChangesAsync with an Npgsql message that names neither this type nor the property.
    public DateTimeOffset SentAt { get; init; } = SentAt.Offset == TimeSpan.Zero
        ? SentAt
        : throw new ArgumentException($"must be UTC (Offset == TimeSpan.Zero), but was {SentAt.Offset}.", nameof(SentAt));
}
