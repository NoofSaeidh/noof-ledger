namespace Noof.Ledger.Application.Capture;

public sealed record CapturedMessage(long ChatId, int MessageId, string Text, DateTimeOffset SentAt)
{
    // Postgres timestamptz round-trips no offset of its own; a non-UTC SentAt would otherwise
    // sail through construction and only fail much later, inside EfCaptureStore.CaptureAsync's
    // SaveChangesAsync, with an Npgsql message that names neither CapturedMessage nor SentAt.
    // Reject it here instead, at the one place every caller constructs this value.
    public DateTimeOffset SentAt { get; init; } = SentAt.Offset == TimeSpan.Zero
        ? SentAt
        : throw new ArgumentException($"must be UTC (Offset == TimeSpan.Zero), but was {SentAt.Offset}.", nameof(SentAt));
}
