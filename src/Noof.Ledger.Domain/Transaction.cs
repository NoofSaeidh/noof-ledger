namespace Noof.Ledger.Domain;

public sealed class Transaction
{
    public required Guid Id { get; init; }
    public required Guid WalletId { get; init; }

    // Quote-and-verify re-reads this, possibly hours after capture, so it must
    // survive untouched regardless of what categorization does to the line items.
    public required string RawText { get; set; }

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

    public required DateTimeOffset CreatedAt { get; init; }
}
