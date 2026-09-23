namespace Noof.Ledger.Domain;

public sealed class Transaction
{
    public required Guid Id { get; init; }
    public required Guid WalletId { get; init; }

    // Null only while a voice capture waits for its transcript (or when none ever came); a check
    // constraint requires it for every text capture.
    public required string? RawText { get; set; }

    public CaptureKind CaptureKind { get; init; }

    // Telegram's id for the voice note. It is enough to download the audio again, so the audio itself is never stored.
    public string? VoiceFileId { get; init; }

    public int? VoiceDurationSeconds { get; init; }

    public required TransactionStatus Status { get; set; }

    // IANA id (e.g. "Europe/Belgrade"), stamped from Capture:TimeZone at capture.
    public required string TimeZoneId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    // The local day the purchase happened. OccurredAt stays the instant the message was sent; when the two
    // name the same local day the time of day is known, otherwise only the day is (decision D3).
    public required DateOnly OccurredOn { get; set; }

    public required long TelegramChatId { get; init; }

    // The user's own message.
    public required int TelegramMessageId { get; init; }

    // Our reply, attached after capture and edited as categorization finishes -
    // unlike TelegramMessageId, it starts unset.
    public int? BotMessageId { get; set; }

    // The Edit prompt. Telegram nests reply_to_message one level only, so a reply to the prompt can be
    // traced back to this record only through this column.
    public int? PromptMessageId { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }
}
